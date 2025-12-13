using System;
using LiteNetLib.Utils;

namespace VoiceCraft.Core.Network.VcPackets.Response
{
    public class VcAcceptResponsePacket : IVoiceCraftPacket, IVoiceCraftRIdPacket
    {
        public VcAcceptResponsePacket() : this(Guid.Empty)
        {
        }

        public VcAcceptResponsePacket(Guid requestId)
        {
            RequestId = requestId;
        }

        public VcPacketType PacketType => VcPacketType.AcceptResponse;

        public Guid RequestId { get; private set; }
        public byte[] PublicKey { get; private set; } = Array.Empty<byte>();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(RequestId);
            writer.PutBytesWithLength(PublicKey);
        }

        public void Deserialize(NetDataReader reader)
        {
            RequestId = reader.GetGuid();
            PublicKey = reader.GetBytesWithLength();
        }

        public VcAcceptResponsePacket Set(Guid requestId, byte[]? publicKey = null)
        {
            RequestId = requestId;
            PublicKey = publicKey ?? Array.Empty<byte>();
            return this;
        }
    }
}