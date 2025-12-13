//////////////////////////////////////////////////////////////////////////////////////////////////////
// VoiceCraft - Unit Tests for Adaptive Jitter Buffer                                             //
//////////////////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.Threading;
using Xunit;

namespace VoiceCraft.Tests;

public class AdaptiveJitterBufferTests
{
    [Fact]
    public void Add_SinglePacket_CanBeRetrieved()
    {
        // Arrange
        var buffer = new TestableAdaptiveJitterBuffer(minBufferMs: 0, maxBufferMs: 200);
        var data = new byte[] { 1, 2, 3, 4, 5 };
        
        // Act
        buffer.Add(1, data, data.Length);
        Thread.Sleep(50); // Wait for buffer to be ready
        var result = buffer.TryGet(out var packet, out _);
        
        // Assert
        Assert.True(result);
        Assert.NotNull(packet);
        Assert.Equal(1, packet!.SequenceId);
        Assert.Equal(data.Length, packet.DataLength);
        packet.Dispose();
    }

    [Fact]
    public void Add_MultiplePackets_ReturnsInOrder()
    {
        // Arrange
        var buffer = new TestableAdaptiveJitterBuffer(minBufferMs: 0, maxBufferMs: 200);
        
        // Act - Add packets out of order
        buffer.Add(3, new byte[] { 3 }, 1);
        buffer.Add(1, new byte[] { 1 }, 1);
        buffer.Add(2, new byte[] { 2 }, 1);
        
        Thread.Sleep(50);
        
        // Assert - Should return in sequence order (oldest first)
        Assert.True(buffer.TryGet(out var p1, out _));
        Assert.Equal(1, p1!.SequenceId);
        p1.Dispose();
        
        Assert.True(buffer.TryGet(out var p2, out _));
        Assert.Equal(2, p2!.SequenceId);
        p2.Dispose();
        
        Assert.True(buffer.TryGet(out var p3, out _));
        Assert.Equal(3, p3!.SequenceId);
        p3.Dispose();
    }

    [Fact]
    public void Add_DuplicateSequence_DropsSecondPacket()
    {
        // Arrange
        var buffer = new TestableAdaptiveJitterBuffer(minBufferMs: 0, maxBufferMs: 200);
        
        // Act
        buffer.Add(1, new byte[] { 1 }, 1);
        buffer.Add(1, new byte[] { 2 }, 1); // Duplicate
        buffer.Add(2, new byte[] { 3 }, 1);
        
        Thread.Sleep(50);
        
        // Assert - Only one packet should be retrievable
        Assert.True(buffer.TryGet(out var p1, out _));
        Assert.Equal(1, p1!.Data[0]); // Should be first packet's data
        p1.Dispose();

        Assert.True(buffer.TryGet(out var p2, out _));
        Assert.Equal(3, p2!.Data[0]);
        p2.Dispose();

        Assert.False(buffer.TryGet(out _, out _));
    }

    [Fact]
    public void Statistics_TracksPacketsCorrectly()
    {
        // Arrange
        var buffer = new TestableAdaptiveJitterBuffer(minBufferMs: 0, maxBufferMs: 200);
        
        // Act
        buffer.Add(1, new byte[] { 1 }, 1);
        buffer.Add(2, new byte[] { 2 }, 1);
        buffer.Add(1, new byte[] { 1 }, 1); // Duplicate
        
        // Assert
        Assert.Equal(3, buffer.Statistics.PacketsReceived);
        Assert.True(buffer.Statistics.PacketsDroppedDuplicate >= 1);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Arrange
        var buffer = new TestableAdaptiveJitterBuffer(minBufferMs: 0, maxBufferMs: 200);
        buffer.Add(1, new byte[] { 1 }, 1);
        buffer.Add(2, new byte[] { 2 }, 1);
        
        // Act
        buffer.Reset();
        
        // Assert
        Assert.Equal(0, buffer.BufferedPackets);
        Assert.False(buffer.TryGet(out _, out _));
    }

    [Fact]
    public void SequenceWrapAround_HandledCorrectly()
    {
        // Arrange
        var buffer = new TestableAdaptiveJitterBuffer(minBufferMs: 0, maxBufferMs: 200);
        
        // Act - Add packets around wraparound point
        buffer.Add(65534, new byte[] { 1 }, 1);
        buffer.Add(65535, new byte[] { 2 }, 1);
        buffer.Add(0, new byte[] { 3 }, 1);
        buffer.Add(1, new byte[] { 4 }, 1);
        
        Thread.Sleep(50);
        
        // Assert - Should handle wraparound correctly
        Assert.True(buffer.TryGet(out var p1, out _));
        Assert.Equal(65534, p1!.SequenceId);
        p1.Dispose();
    }
}

/// <summary>
/// Testable version of AdaptiveJitterBuffer with reduced delays
/// </summary>
internal class TestableAdaptiveJitterBuffer : IDisposable
{
    private readonly VoiceCraft.Client.Network.AdaptiveJitterBuffer _buffer;
    
    public TestableAdaptiveJitterBuffer(int minBufferMs, int maxBufferMs)
    {
        _buffer = new VoiceCraft.Client.Network.AdaptiveJitterBuffer(minBufferMs, maxBufferMs, 20);
    }
    
    public VoiceCraft.Client.Network.JitterBufferStatistics Statistics => _buffer.Statistics;
    public int BufferedPackets => _buffer.BufferedPackets;
    
    public void Add(ushort sequenceId, byte[] data, int length) => _buffer.Add(sequenceId, data, length);
    public bool TryGet(out VoiceCraft.Client.Network.AdaptiveJitterPacket? packet, out bool isLost) 
        => _buffer.TryGet(out packet, out isLost);
    public void Reset() => _buffer.Reset();
    public void Dispose() => _buffer.Dispose();
}
