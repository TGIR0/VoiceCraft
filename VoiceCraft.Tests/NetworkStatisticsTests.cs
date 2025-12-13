//////////////////////////////////////////////////////////////////////////////////////////////////////
// VoiceCraft - Unit Tests for Network Statistics                                                 //
//////////////////////////////////////////////////////////////////////////////////////////////////////

using VoiceCraft.Core.Network;
using Xunit;

namespace VoiceCraft.Tests;

public class NetworkStatisticsTests
{
    [Fact]
    public void RecordRtt_UpdatesRttValue()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordRtt(50.0);
        
        // Assert
        Assert.True(stats.RttMs > 0);
    }

    [Fact]
    public void RecordRtt_TracksMinMax()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordRtt(100.0);
        stats.RecordRtt(50.0);
        stats.RecordRtt(150.0);
        
        // Assert
        Assert.Equal(50.0, stats.RttMinMs);
        Assert.Equal(150.0, stats.RttMaxMs);
    }

    [Fact]
    public void RecordPacketSent_IncrementsCounts()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordPacketSent(100);
        stats.RecordPacketSent(200);
        
        // Assert
        Assert.Equal(2, stats.PacketsSent);
        Assert.Equal(300, stats.BytesSent);
    }

    [Fact]
    public void RecordPacketReceived_IncrementsCounts()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordPacketReceived(100);
        stats.RecordPacketReceived(200);
        
        // Assert
        Assert.Equal(2, stats.PacketsReceived);
        Assert.Equal(300, stats.BytesReceived);
    }

    [Fact]
    public void RecordPacketLost_IncrementsCounts()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordPacketSent(100);
        stats.RecordPacketSent(100);
        stats.RecordPacketLost();
        
        // Assert
        Assert.Equal(1, stats.PacketsLost);
        Assert.Equal(50.0, stats.PacketLossPercent);
    }

    [Fact]
    public void Quality_ReturnsExcellent_ForGoodConditions()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordRtt(30.0); // Low RTT
        // No packet loss
        
        // Assert
        Assert.Equal(NetworkQuality.Excellent, stats.Quality);
    }

    [Fact]
    public void Quality_ReturnsBad_ForPoorConditions()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordRtt(500.0); // High RTT
        for (int i = 0; i < 10; i++) stats.RecordPacketSent(100);
        for (int i = 0; i < 2; i++) stats.RecordPacketLost();
        
        // Assert
        Assert.True(stats.Quality >= NetworkQuality.Poor);
    }

    [Fact]
    public void MosScore_InValidRange()
    {
        // Arrange
        var stats = new NetworkStatistics();
        
        // Act
        stats.RecordRtt(100.0);
        
        // Assert
        Assert.InRange(stats.MosScore, 1.0, 5.0);
    }

    [Fact]
    public void GetSnapshot_ReturnsCorrectValues()
    {
        // Arrange
        var stats = new NetworkStatistics();
        stats.RecordRtt(50.0);
        stats.RecordPacketSent(100);
        stats.RecordPacketReceived(200);
        
        // Act
        var snapshot = stats.GetSnapshot();
        
        // Assert
        Assert.True(snapshot.RttMs > 0);
        Assert.Equal(1, snapshot.PacketsSent);
        Assert.Equal(1, snapshot.PacketsReceived);
        Assert.Equal(100, snapshot.BytesSent);
        Assert.Equal(200, snapshot.BytesReceived);
    }

    [Fact]
    public void Reset_ClearsAllStatistics()
    {
        // Arrange
        var stats = new NetworkStatistics();
        stats.RecordRtt(100.0);
        stats.RecordPacketSent(100);
        stats.RecordPacketReceived(200);
        stats.RecordPacketLost();
        
        // Act
        stats.Reset();
        
        // Assert
        Assert.Equal(0, stats.RttMs);
        Assert.Equal(0, stats.PacketsSent);
        Assert.Equal(0, stats.PacketsReceived);
        Assert.Equal(0, stats.PacketsLost);
        Assert.Equal(0, stats.BytesSent);
        Assert.Equal(0, stats.BytesReceived);
    }

    [Fact]
    public void Snapshot_ToString_ReturnsFormattedString()
    {
        // Arrange
        var stats = new NetworkStatistics();
        stats.RecordRtt(50.0);
        
        // Act
        var snapshot = stats.GetSnapshot();
        var str = snapshot.ToString();
        
        // Assert
        Assert.Contains("Quality:", str);
        Assert.Contains("RTT:", str);
        Assert.Contains("MOS:", str);
    }
}
