//////////////////////////////////////////////////////////////////////////////////////////////////////
// VoiceCraft - State-of-the-Art Adaptive Jitter Buffer Implementation                            //
// Features: Dynamic delay adjustment, packet loss concealment, network statistics                //
//////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace VoiceCraft.Client.Network;

/// <summary>
/// Statistics for monitoring jitter buffer performance
/// </summary>
public sealed class JitterBufferStatistics
{
    private long _packetsReceived;
    private long _packetsLost;
    private long _packetsDroppedLate;
    private long _packetsDroppedDuplicate;
    private long _packetsOutOfOrder;
    private double _averageJitterMs;
    private double _maxJitterMs;
    private int _currentBufferDepth;
    private int _targetBufferDepth;

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
    public long PacketsLost => Interlocked.Read(ref _packetsLost);
    public long PacketsDroppedLate => Interlocked.Read(ref _packetsDroppedLate);
    public long PacketsDroppedDuplicate => Interlocked.Read(ref _packetsDroppedDuplicate);
    public long PacketsOutOfOrder => Interlocked.Read(ref _packetsOutOfOrder);
    public double AverageJitterMs => Volatile.Read(ref _averageJitterMs);
    public double MaxJitterMs => Volatile.Read(ref _maxJitterMs);
    public int CurrentBufferDepth => Volatile.Read(ref _currentBufferDepth);
    public int TargetBufferDepth => Volatile.Read(ref _targetBufferDepth);
    
    public double PacketLossRate => PacketsReceived > 0 
        ? (double)PacketsLost / (PacketsReceived + PacketsLost) * 100.0 
        : 0.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementReceived() => Interlocked.Increment(ref _packetsReceived);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementLost() => Interlocked.Increment(ref _packetsLost);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementDroppedLate() => Interlocked.Increment(ref _packetsDroppedLate);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementDroppedDuplicate() => Interlocked.Increment(ref _packetsDroppedDuplicate);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementOutOfOrder() => Interlocked.Increment(ref _packetsOutOfOrder);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void UpdateJitter(double jitterMs)
    {
        // Exponential moving average for jitter
        var currentAvg = Volatile.Read(ref _averageJitterMs);
        var newAvg = currentAvg * 0.875 + jitterMs * 0.125; // α = 0.125 (RFC 3550)
        Volatile.Write(ref _averageJitterMs, newAvg);
        
        var currentMax = Volatile.Read(ref _maxJitterMs);
        if (jitterMs > currentMax)
            Volatile.Write(ref _maxJitterMs, jitterMs);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void UpdateBufferDepth(int current, int target)
    {
        Volatile.Write(ref _currentBufferDepth, current);
        Volatile.Write(ref _targetBufferDepth, target);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _packetsReceived, 0);
        Interlocked.Exchange(ref _packetsLost, 0);
        Interlocked.Exchange(ref _packetsDroppedLate, 0);
        Interlocked.Exchange(ref _packetsDroppedDuplicate, 0);
        Interlocked.Exchange(ref _packetsOutOfOrder, 0);
        Volatile.Write(ref _averageJitterMs, 0.0);
        Volatile.Write(ref _maxJitterMs, 0.0);
        Volatile.Write(ref _currentBufferDepth, 0);
        Volatile.Write(ref _targetBufferDepth, 0);
    }
}

/// <summary>
/// Pooled packet structure to reduce GC pressure
/// </summary>
public sealed class AdaptiveJitterPacket : IDisposable
{
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;
    
    public ushort SequenceId { get; private set; }
    public byte[] Data { get; private set; }
    public int DataLength { get; private set; }
    public DateTime ReceivedTime { get; private set; }
    public long ReceivedTicks { get; private set; }
    
    private bool _isPooled;
    private bool _disposed;

    private AdaptiveJitterPacket() 
    {
        Data = Array.Empty<byte>();
    }

    public static AdaptiveJitterPacket Create(ushort sequenceId, byte[] data, int length)
    {
        var packet = new AdaptiveJitterPacket
        {
            SequenceId = sequenceId,
            DataLength = length,
            ReceivedTime = DateTime.UtcNow,
            ReceivedTicks = Environment.TickCount64,
            _isPooled = true
        };
        
        // Rent from pool
        packet.Data = Pool.Rent(length);
        Buffer.BlockCopy(data, 0, packet.Data, 0, length);
        
        return packet;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        if (_isPooled && Data.Length > 0)
        {
            Pool.Return(Data, clearArray: true);
            Data = Array.Empty<byte>();
        }
    }
}

/// <summary>
/// State-of-the-Art Adaptive Jitter Buffer with dynamic delay adjustment
/// </summary>
public sealed class AdaptiveJitterBuffer : IDisposable
{
    // Configuration
    private readonly int _minBufferMs;
    private readonly int _maxBufferMs;
    private readonly int _frameSizeMs;
    private readonly int _maxPackets;
    
    // State
    private readonly LinkedList<AdaptiveJitterPacket> _buffer = new();
    private readonly object _lock = new();
    private readonly JitterBufferStatistics _statistics = new();
    
    private ushort? _lastPlayedSeqId;
    private ushort? _expectedSeqId;
    private long _lastPacketTicks;
    private int _targetDelayMs;
    private int _adaptiveDelayMs;
    private bool _disposed;
    
    // Sequence tracking for wraparound
    private const ushort SeqNumHalf = ushort.MaxValue / 2;
    private const ushort SeqNumQuarter = ushort.MaxValue / 4;
    
    public JitterBufferStatistics Statistics => _statistics;
    public int CurrentDelayMs => _adaptiveDelayMs;
    public int BufferedPackets { get { lock (_lock) return _buffer.Count; } }

    /// <summary>
    /// Creates a new adaptive jitter buffer
    /// </summary>
    /// <param name="minBufferMs">Minimum buffer delay in milliseconds</param>
    /// <param name="maxBufferMs">Maximum buffer delay in milliseconds</param>
    /// <param name="frameSizeMs">Audio frame size in milliseconds (default 20ms)</param>
    public AdaptiveJitterBuffer(int minBufferMs = 40, int maxBufferMs = 200, int frameSizeMs = 20)
    {
        _minBufferMs = Math.Max(frameSizeMs, minBufferMs);
        _maxBufferMs = Math.Max(_minBufferMs * 2, maxBufferMs);
        _frameSizeMs = frameSizeMs;
        _maxPackets = _maxBufferMs / frameSizeMs + 2;
        _targetDelayMs = _minBufferMs;
        _adaptiveDelayMs = _minBufferMs;
    }

    /// <summary>
    /// Adds a packet to the jitter buffer with adaptive delay adjustment
    /// </summary>
    public void Add(ushort sequenceId, byte[] data, int length)
    {
        if (_disposed) return;
        
        var packet = AdaptiveJitterPacket.Create(sequenceId, data, length);
        
        lock (_lock)
        {
            _statistics.IncrementReceived();
            
            // Calculate jitter from inter-arrival time
            if (_lastPacketTicks > 0)
            {
                var interArrivalMs = (Environment.TickCount64 - _lastPacketTicks);
                var expectedIntervalMs = _frameSizeMs;
                var jitterMs = Math.Abs(interArrivalMs - expectedIntervalMs);
                _statistics.UpdateJitter(jitterMs);
                
                // Adaptive delay adjustment based on jitter
                AdaptDelay(jitterMs);
            }
            _lastPacketTicks = packet.ReceivedTicks;
            
            // Check for duplicate
            if (_lastPlayedSeqId.HasValue && !IsSequenceNewer(sequenceId, _lastPlayedSeqId.Value))
            {
                _statistics.IncrementDroppedDuplicate();
                packet.Dispose();
                return;
            }
            
            // Check for too late packet
            if (_expectedSeqId.HasValue && !IsSequenceNewer(sequenceId, _expectedSeqId.Value) 
                && SequenceDistance(sequenceId, _expectedSeqId.Value) > _maxPackets)
            {
                _statistics.IncrementDroppedLate();
                packet.Dispose();
                return;
            }
            
            // Detect out of order
            if (_buffer.Count > 0 && IsSequenceNewer(_buffer.Last!.Value.SequenceId, sequenceId))
            {
                _statistics.IncrementOutOfOrder();
            }
            
            // Insert in sorted order (newest at front, oldest at back)
            InsertSorted(packet);
            
            // Trim buffer if too large
            while (_buffer.Count > _maxPackets)
            {
                var oldest = _buffer.Last!.Value;
                _buffer.RemoveLast();
                oldest.Dispose();
                _statistics.IncrementDroppedLate();
            }
            
            _statistics.UpdateBufferDepth(_buffer.Count, _targetDelayMs / _frameSizeMs);
        }
    }

    /// <summary>
    /// Gets the next packet from the buffer, handling packet loss concealment
    /// </summary>
    public bool TryGet([NotNullWhen(true)] out AdaptiveJitterPacket? packet, out bool isLost)
    {
        packet = null;
        isLost = false;
        
        if (_disposed) return false;
        
        lock (_lock)
        {
            if (_buffer.Count == 0) return false;
            
            var oldest = _buffer.Last!.Value;
            
            // Check if we should wait (buffer not filled to target)
            var bufferTimeMs = _buffer.Count * _frameSizeMs;
            var timeSinceReceived = (Environment.TickCount64 - oldest.ReceivedTicks);
            
            // Initial buffering: wait until we have enough packets
            if (!_expectedSeqId.HasValue && bufferTimeMs < _adaptiveDelayMs)
            {
                return false;
            }
            
            // Ongoing playback: check if we have the expected sequence
            if (_expectedSeqId.HasValue)
            {
                // Check if oldest packet is the expected one
                if (oldest.SequenceId == _expectedSeqId.Value)
                {
                    packet = oldest;
                    _buffer.RemoveLast();
                    _lastPlayedSeqId = packet.SequenceId;
                    _expectedSeqId = (ushort)(packet.SequenceId + 1);
                    _statistics.UpdateBufferDepth(_buffer.Count, _targetDelayMs / _frameSizeMs);
                    return true;
                }
                
                // Packet loss detection
                if (IsSequenceNewer(oldest.SequenceId, _expectedSeqId.Value))
                {
                    // Check if we've waited long enough
                    var waitThresholdMs = _adaptiveDelayMs;
                    if (timeSinceReceived >= waitThresholdMs)
                    {
                        // Lost packet - signal for PLC (Packet Loss Concealment)
                        isLost = true;
                        _statistics.IncrementLost();
                        _expectedSeqId = (ushort)(_expectedSeqId.Value + 1);
                        return false;
                    }
                    return false; // Wait more
                }
                
                // Older packet than expected (shouldn't happen, but handle it)
                packet = oldest;
                _buffer.RemoveLast();
                _lastPlayedSeqId = packet.SequenceId;
                _expectedSeqId = (ushort)(packet.SequenceId + 1);
                _statistics.UpdateBufferDepth(_buffer.Count, _targetDelayMs / _frameSizeMs);
                return true;
            }
            else
            {
                // First packet
                packet = oldest;
                _buffer.RemoveLast();
                _lastPlayedSeqId = packet.SequenceId;
                _expectedSeqId = (ushort)(packet.SequenceId + 1);
                _statistics.UpdateBufferDepth(_buffer.Count, _targetDelayMs / _frameSizeMs);
                return true;
            }
        }
    }

    /// <summary>
    /// Legacy Get method for backward compatibility
    /// </summary>
    public bool Get([NotNullWhen(true)] out AdaptiveJitterPacket? packet)
    {
        return TryGet(out packet, out _);
    }

    /// <summary>
    /// Resets the buffer state
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            foreach (var packet in _buffer)
                packet.Dispose();
            
            _buffer.Clear();
            _lastPlayedSeqId = null;
            _expectedSeqId = null;
            _lastPacketTicks = 0;
            _adaptiveDelayMs = _minBufferMs;
            _statistics.Reset();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    #region Private Methods
    
    private void AdaptDelay(double jitterMs)
    {
        // RFC 3550 inspired adaptive algorithm
        // Increase buffer when jitter is high, decrease when stable
        
        var targetPackets = Math.Max(2, (int)Math.Ceiling((_statistics.AverageJitterMs * 2) / _frameSizeMs));
        var newTargetMs = Math.Clamp(targetPackets * _frameSizeMs, _minBufferMs, _maxBufferMs);
        
        // Smooth transition
        if (newTargetMs > _targetDelayMs)
        {
            // Quick increase
            _targetDelayMs = Math.Min(_targetDelayMs + _frameSizeMs, newTargetMs);
        }
        else if (newTargetMs < _targetDelayMs)
        {
            // Slow decrease
            _targetDelayMs = Math.Max(_targetDelayMs - 1, newTargetMs);
        }
        
        // Apply to adaptive delay with smoothing
        _adaptiveDelayMs = (_adaptiveDelayMs * 7 + _targetDelayMs) / 8;
    }
    
    private void InsertSorted(AdaptiveJitterPacket packet)
    {
        if (_buffer.Count == 0)
        {
            _buffer.AddFirst(packet);
            return;
        }
        
        // Find correct position (sorted by sequence, newest first)
        var node = _buffer.First;
        while (node != null)
        {
            if (IsSequenceNewer(packet.SequenceId, node.Value.SequenceId))
            {
                _buffer.AddBefore(node, packet);
                return;
            }
            
            // Duplicate check
            if (packet.SequenceId == node.Value.SequenceId)
            {
                _statistics.IncrementDroppedDuplicate();
                packet.Dispose();
                return;
            }
            
            node = node.Next;
        }
        
        // Oldest packet, add to end
        _buffer.AddLast(packet);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSequenceNewer(ushort seq1, ushort seq2)
    {
        // Handle wraparound: seq1 is newer if distance is < half of range
        var diff = (ushort)(seq1 - seq2);
        return diff > 0 && diff < SeqNumHalf;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SequenceDistance(ushort seq1, ushort seq2)
    {
        var diff = (int)seq1 - seq2;
        if (diff < 0) diff += ushort.MaxValue + 1;
        if (diff > SeqNumHalf) diff = ushort.MaxValue + 1 - diff;
        return diff;
    }
    
    #endregion
}

/// <summary>
/// Legacy JitterPacket wrapper for backward compatibility
/// </summary>
public class JitterPacket
{
    public readonly byte[] Data;
    public readonly DateTime ReceivedTime;
    public readonly ushort SequenceId;
    
    public JitterPacket(ushort sequenceId, byte[] data)
    {
        SequenceId = sequenceId;
        Data = data;
        ReceivedTime = DateTime.UtcNow;
    }
    
    internal JitterPacket(AdaptiveJitterPacket adaptive)
    {
        SequenceId = adaptive.SequenceId;
        ReceivedTime = adaptive.ReceivedTime;
        // Copy data from pooled array
        Data = new byte[adaptive.DataLength];
        Buffer.BlockCopy(adaptive.Data, 0, Data, 0, adaptive.DataLength);
    }
}

/// <summary>
/// Legacy JitterBuffer wrapper for backward compatibility
/// </summary>
public class JitterBuffer
{
    private readonly AdaptiveJitterBuffer _adaptive;
    
    public JitterBuffer(TimeSpan maxDropOutTime)
    {
        var maxMs = (int)maxDropOutTime.TotalMilliseconds;
        _adaptive = new AdaptiveJitterBuffer(40, Math.Max(100, maxMs));
    }
    
    public JitterBufferStatistics Statistics => _adaptive.Statistics;
    
    public bool Get([NotNullWhen(true)] out JitterPacket? packet)
    {
        packet = null;
        if (!_adaptive.TryGet(out var adaptive, out _))
        {
            return false;
        }
        
        packet = new JitterPacket(adaptive);
        adaptive.Dispose();
        return true;
    }
    
    public void Add(JitterPacket current)
    {
        _adaptive.Add(current.SequenceId, current.Data, current.Data.Length);
    }
    
    public void Reset()
    {
        _adaptive.Reset();
    }
}
