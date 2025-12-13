using System;
using LiteNetLib.Utils;

namespace VoiceCraft.Core.Network.VcPackets
{
    public class VcEncryptedPacket : IVoiceCraftPacket
    {
        public VcPacketType PacketType => VcPacketType.EncryptedPacket;

        public byte[] Iv { get; private set; } = Array.Empty<byte>();
        public byte[] Tag { get; private set; } = Array.Empty<byte>();
        public byte[] EncryptedData { get; private set; } = Array.Empty<byte>();

        public void Serialize(NetDataWriter writer)
        {
            writer.PutBytesWithLength(Iv);
            writer.PutBytesWithLength(Tag);
            writer.PutBytesWithLength(EncryptedData);
        }

        public void Deserialize(NetDataReader reader)
        {
            Iv = reader.GetBytesWithLength();
            Tag = reader.GetBytesWithLength();
            EncryptedData = reader.GetBytesWithLength();
        }

        public VcEncryptedPacket Set(byte[] iv, byte[] tag, byte[] encryptedData)
        {
            Iv = iv;
            Tag = tag;
            EncryptedData = encryptedData;
            return this;
        }
    }
}
