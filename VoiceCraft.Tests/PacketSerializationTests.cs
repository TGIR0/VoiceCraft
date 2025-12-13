using LiteNetLib.Utils;
using VoiceCraft.Core;
using VoiceCraft.Core.Network.VcPackets;
using VoiceCraft.Core.Network.VcPackets.Request;

namespace VoiceCraft.Tests;

public class PacketSerializationTests
{
    [Fact]
    public void VcAudioRequestPacket_SerializeDeserialize_ShouldMatch()
    {
        // Arrange
        var originalPacket = PacketPool<VcAudioRequestPacket>.GetPacket();
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var encodedData = new byte[] { 10, 20, 30 };
        ushort timestamp = 12345;
        float frameLoudness = 0.5f;
        
        // Note: The Set method in VcAudioRequestPacket takes (timestamp, frameLoudness, bytesEncoded, encodeBuffer)
        // We need to match the signature.
        originalPacket.Set(timestamp, frameLoudness, encodedData.Length, encodedData);

        var writer = new NetDataWriter();
        
        // Act
        originalPacket.Serialize(writer);
        
        var reader = new NetDataReader();
        reader.SetSource(writer.CopyData());
        
        var deserializedPacket = PacketPool<VcAudioRequestPacket>.GetPacket();
        deserializedPacket.Deserialize(reader);

        // Assert
        Assert.Equal(timestamp, deserializedPacket.Timestamp);
        Assert.Equal(frameLoudness, deserializedPacket.FrameLoudness);
        Assert.Equal(encodedData.Length, deserializedPacket.Data.Length);
        Assert.Equal(encodedData, deserializedPacket.Data);
        
        // Cleanup
        PacketPool<VcAudioRequestPacket>.Return(originalPacket);
        PacketPool<VcAudioRequestPacket>.Return(deserializedPacket);
    }
}
