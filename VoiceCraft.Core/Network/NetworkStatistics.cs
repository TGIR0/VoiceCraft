//////////////////////////////////////////////////////////////////////////////////////////////////////
// VoiceCraft - State-of-the-Art Network Statistics and Quality Monitoring                        //
// Features: RTT, Packet Loss, Bandwidth, Quality Score, Adaptive Metrics                        //
//////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace VoiceCraft.Core.Network
{
    /// <summary>
    /// Network quality level enumeration
    /// </summary>
    public enum NetworkQuality
    {
        Excellent,  // < 50ms RTT, < 1% loss
        Good,       // < 100ms RTT, < 3% loss
        Fair,       // < 200ms RTT, < 5% loss
        Poor,       // < 400ms RTT, < 10% loss
        Bad         // >= 400ms RTT or >= 10% loss
    }

    /// <summary>
    /// Comprehensive network statistics tracking for VoIP quality monitoring
    /// </summary>
    public sealed class NetworkStatistics
    {
        // RTT (Round-Trip Time) tracking
        private double _rttMs;
        private double _rttMinMs = double.MaxValue;
        private double _rttMaxMs;
        private double _rttVariance;
        private long _rttSampleCount;
        
        // Packet statistics
        private long _packetsSent;
        private long _packetsReceived;
        private long _packetsLost;
        private long _packetsOutOfOrder;
        private long _bytesReceived;
        private long _bytesSent;
        
        // Bandwidth tracking
        private long _lastBandwidthCheckTicks;
        private long _lastBytesReceived;
        private long _lastBytesSent;
        private double _inboundBandwidthKbps;
        private double _outboundBandwidthKbps;
        
        // Jitter tracking (RFC 3550)
        private double _jitterMs;
        private long _lastPacketTimestamp;
        private long _lastArrivalTime;
        
        // Quality metrics
        private double _mosScore = 4.5; // Mean Opinion Score (1-5)
        
        // Synchronization
        private readonly object _lock = new object();

        #region Public Properties

        /// <summary>
        /// Current smoothed Round-Trip Time in milliseconds
        /// </summary>
        public double RttMs => Volatile.Read(ref _rttMs);
        
        /// <summary>
        /// Minimum observed RTT in milliseconds
        /// </summary>
        public double RttMinMs => Volatile.Read(ref _rttMinMs) == double.MaxValue ? 0 : Volatile.Read(ref _rttMinMs);
        
        /// <summary>
        /// Maximum observed RTT in milliseconds
        /// </summary>
        public double RttMaxMs => Volatile.Read(ref _rttMaxMs);
        
        /// <summary>
        /// RTT variance (jitter) in milliseconds
        /// </summary>
        public double RttVarianceMs => Volatile.Read(ref _rttVariance);
        
        /// <summary>
        /// Total packets sent
        /// </summary>
        public long PacketsSent => Interlocked.Read(ref _packetsSent);
        
        /// <summary>
        /// Total packets received
        /// </summary>
        public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
        
        /// <summary>
        /// Total packets lost
        /// </summary>
        public long PacketsLost => Interlocked.Read(ref _packetsLost);
        
        /// <summary>
        /// Packets received out of order
        /// </summary>
        public long PacketsOutOfOrder => Interlocked.Read(ref _packetsOutOfOrder);
        
        /// <summary>
        /// Total bytes received
        /// </summary>
        public long BytesReceived => Interlocked.Read(ref _bytesReceived);
        
        /// <summary>
        /// Total bytes sent
        /// </summary>
        public long BytesSent => Interlocked.Read(ref _bytesSent);
        
        /// <summary>
        /// Current inbound bandwidth in Kbps
        /// </summary>
        public double InboundBandwidthKbps => Volatile.Read(ref _inboundBandwidthKbps);
        
        /// <summary>
        /// Current outbound bandwidth in Kbps
        /// </summary>
        public double OutboundBandwidthKbps => Volatile.Read(ref _outboundBandwidthKbps);
        
        /// <summary>
        /// Current jitter in milliseconds (interarrival jitter per RFC 3550)
        /// </summary>
        public double JitterMs => Volatile.Read(ref _jitterMs);
        
        /// <summary>
        /// Estimated Mean Opinion Score (1.0 - 5.0)
        /// </summary>
        public double MosScore => Volatile.Read(ref _mosScore);
        
        /// <summary>
        /// Packet loss percentage
        /// </summary>
        public double PacketLossPercent
        {
            get
            {
                var total = PacketsSent;
                if (total == 0) return 0;
                return (double)PacketsLost / total * 100.0;
            }
        }
        
        /// <summary>
        /// Current network quality assessment
        /// </summary>
        public NetworkQuality Quality => CalculateQuality();

        #endregion

        #region Recording Methods

        /// <summary>
        /// Records a new RTT sample
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordRtt(double rttMs)
        {
            lock (_lock)
            {
                _rttSampleCount++;
                
                // Exponential moving average (α = 0.125 per RFC 6298)
                if (_rttSampleCount == 1)
                {
                    _rttMs = rttMs;
                    _rttVariance = rttMs / 2;
                }
                else
                {
                    var delta = rttMs - _rttMs;
                    _rttMs = _rttMs + 0.125 * delta;
                    _rttVariance = _rttVariance + 0.25 * (Math.Abs(delta) - _rttVariance);
                }
                
                // Track min/max
                if (rttMs < _rttMinMs) _rttMinMs = rttMs;
                if (rttMs > _rttMaxMs) _rttMaxMs = rttMs;
                
                UpdateMosScore();
            }
        }

        /// <summary>
        /// Records a packet being sent
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordPacketSent(int bytes)
        {
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, bytes);
        }

        /// <summary>
        /// Records a packet being received with timing for jitter calculation
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordPacketReceived(int bytes, long packetTimestamp = 0)
        {
            var arrivalTime = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            
            Interlocked.Increment(ref _packetsReceived);
            Interlocked.Add(ref _bytesReceived, bytes);
            
            // Calculate jitter (RFC 3550 algorithm)
            if (packetTimestamp > 0 && _lastPacketTimestamp > 0)
            {
                var transitTime = arrivalTime - packetTimestamp;
                var lastTransitTime = _lastArrivalTime - _lastPacketTimestamp;
                var d = Math.Abs(transitTime - lastTransitTime);
                
                // J = J + (|D| - J) / 16
                var currentJitter = Volatile.Read(ref _jitterMs);
                var newJitter = currentJitter + (d - currentJitter) / 16.0;
                Volatile.Write(ref _jitterMs, newJitter);
            }
            
            _lastPacketTimestamp = packetTimestamp;
            _lastArrivalTime = arrivalTime;
        }

        /// <summary>
        /// Records packet loss
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordPacketLost(int count = 1)
        {
            Interlocked.Add(ref _packetsLost, count);
            UpdateMosScore();
        }

        /// <summary>
        /// Records out-of-order packet
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordPacketOutOfOrder()
        {
            Interlocked.Increment(ref _packetsOutOfOrder);
        }

        /// <summary>
        /// Updates bandwidth calculations (call periodically, e.g., every second)
        /// </summary>
        public void UpdateBandwidth()
        {
            var currentTicks = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            var elapsedMs = currentTicks - _lastBandwidthCheckTicks;
            
            if (elapsedMs < 100) return; // Minimum 100ms between updates
            
            var currentBytesReceived = Interlocked.Read(ref _bytesReceived);
            var currentBytesSent = Interlocked.Read(ref _bytesSent);
            
            var receivedDelta = currentBytesReceived - _lastBytesReceived;
            var sentDelta = currentBytesSent - _lastBytesSent;
            
            // Calculate Kbps: (bytes * 8) / (ms) = bits/ms = Kbps
            Volatile.Write(ref _inboundBandwidthKbps, (receivedDelta * 8.0) / elapsedMs);
            Volatile.Write(ref _outboundBandwidthKbps, (sentDelta * 8.0) / elapsedMs);
            
            _lastBandwidthCheckTicks = currentTicks;
            _lastBytesReceived = currentBytesReceived;
            _lastBytesSent = currentBytesSent;
        }

        #endregion

        #region Quality Assessment

        /// <summary>
        /// Calculates network quality based on current metrics
        /// </summary>
        private NetworkQuality CalculateQuality()
        {
            var rtt = RttMs;
            var loss = PacketLossPercent;
            var jitter = JitterMs;
            
            // Quality thresholds
            if (rtt < 50 && loss < 1 && jitter < 20)
                return NetworkQuality.Excellent;
            if (rtt < 100 && loss < 3 && jitter < 40)
                return NetworkQuality.Good;
            if (rtt < 200 && loss < 5 && jitter < 70)
                return NetworkQuality.Fair;
            if (rtt < 400 && loss < 10 && jitter < 100)
                return NetworkQuality.Poor;
            
            return NetworkQuality.Bad;
        }

        /// <summary>
        /// Updates the Mean Opinion Score estimation using E-model (ITU-T G.107)
        /// </summary>
        private void UpdateMosScore()
        {
            // Simplified E-model calculation
            // R = 93.2 - Id - Ie + A
            // Id = delay impairment, Ie = equipment impairment, A = advantage factor
            
            var rtt = RttMs;
            var loss = PacketLossPercent;
            var jitter = JitterMs;
            
            // Base R-value
            double r = 93.2;
            
            // Delay impairment (simplified)
            var effectiveLatency = rtt / 2 + jitter * 2; // One-way delay + jitter buffer
            if (effectiveLatency < 160)
                r -= 0.024 * effectiveLatency;
            else
                r -= 0.024 * 160 + 0.11 * (effectiveLatency - 160);
            
            // Packet loss impairment (codec-dependent, using generic values)
            r -= 2.5 * loss; // Rough approximation
            
            // Clamp R-value
            r = Math.Clamp(r, 0, 100);
            
            // Convert R to MOS
            // MOS = 1 + 0.035R + R(R-60)(100-R)*7*10^-6
            double mos;
            if (r < 0)
                mos = 1.0;
            else if (r > 100)
                mos = 4.5;
            else
                mos = 1.0 + 0.035 * r + r * (r - 60) * (100 - r) * 7e-6;
            
            Volatile.Write(ref _mosScore, Math.Clamp(mos, 1.0, 5.0));
        }

        #endregion

        /// <summary>
        /// Resets all statistics
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _rttMs = 0;
                _rttMinMs = double.MaxValue;
                _rttMaxMs = 0;
                _rttVariance = 0;
                _rttSampleCount = 0;
                
                Interlocked.Exchange(ref _packetsSent, 0);
                Interlocked.Exchange(ref _packetsReceived, 0);
                Interlocked.Exchange(ref _packetsLost, 0);
                Interlocked.Exchange(ref _packetsOutOfOrder, 0);
                Interlocked.Exchange(ref _bytesReceived, 0);
                Interlocked.Exchange(ref _bytesSent, 0);
                
                _lastBandwidthCheckTicks = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                _lastBytesReceived = 0;
                _lastBytesSent = 0;
                Volatile.Write(ref _inboundBandwidthKbps, 0);
                Volatile.Write(ref _outboundBandwidthKbps, 0);
                
                Volatile.Write(ref _jitterMs, 0);
                _lastPacketTimestamp = 0;
                _lastArrivalTime = 0;
                
                Volatile.Write(ref _mosScore, 4.5);
            }
        }

        /// <summary>
        /// Creates a snapshot of current statistics for reporting
        /// </summary>
        public NetworkStatisticsSnapshot GetSnapshot()
        {
            return new NetworkStatisticsSnapshot
            {
                RttMs = RttMs,
                RttMinMs = RttMinMs,
                RttMaxMs = RttMaxMs,
                RttVarianceMs = RttVarianceMs,
                JitterMs = JitterMs,
                PacketsSent = PacketsSent,
                PacketsReceived = PacketsReceived,
                PacketsLost = PacketsLost,
                PacketsOutOfOrder = PacketsOutOfOrder,
                PacketLossPercent = PacketLossPercent,
                BytesSent = BytesSent,
                BytesReceived = BytesReceived,
                InboundBandwidthKbps = InboundBandwidthKbps,
                OutboundBandwidthKbps = OutboundBandwidthKbps,
                MosScore = MosScore,
                Quality = Quality,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Immutable snapshot of network statistics
    /// </summary>
    public sealed class NetworkStatisticsSnapshot
    {
        public double RttMs { get; set; }
        public double RttMinMs { get; set; }
        public double RttMaxMs { get; set; }
        public double RttVarianceMs { get; set; }
        public double JitterMs { get; set; }
        public long PacketsSent { get; set; }
        public long PacketsReceived { get; set; }
        public long PacketsLost { get; set; }
        public long PacketsOutOfOrder { get; set; }
        public double PacketLossPercent { get; set; }
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        public double InboundBandwidthKbps { get; set; }
        public double OutboundBandwidthKbps { get; set; }
        public double MosScore { get; set; }
        public NetworkQuality Quality { get; set; }
        public DateTime Timestamp { get; set; }

        public override string ToString()
        {
            return $"Quality: {Quality}, RTT: {RttMs:F1}ms, Jitter: {JitterMs:F1}ms, " +
                   $"Loss: {PacketLossPercent:F2}%, MOS: {MosScore:F2}, " +
                   $"BW: ↓{InboundBandwidthKbps:F1}Kbps ↑{OutboundBandwidthKbps:F1}Kbps";
        }
    }
}
