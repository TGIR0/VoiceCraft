using System;
using System.Numerics;
using LiteNetLib.Utils;

namespace VoiceCraft.Core.Network.VcPackets.Request
{
    [Flags]
    public enum AudioPacketFlags : byte
    {
        None = 0,
        HasPosition = 1 << 0,
        HasRotation = 1 << 1
    }

    public class VcAdvancedAudioPacket : IVoiceCraftPacket
    {
        public VcAdvancedAudioPacket()
        {
        }

        public VcPacketType PacketType => VcPacketType.AdvancedAudio;

        public int EntityId { get; private set; } // Added EntityId
        public ushort Timestamp { get; private set; }
        public float FrameLoudness { get; private set; }
        public int Length { get; private set; }
        public byte[] Data { get; private set; } = Array.Empty<byte>();
        
        // Spatial Data
        public Vector3 Position { get; private set; }
        public Vector2 Rotation { get; private set; }
        public AudioPacketFlags Flags { get; private set; }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(EntityId); // Serialize EntityId
            writer.Put(Timestamp);
            writer.Put(FrameLoudness);
            writer.Put((byte)Flags);
            
            if ((Flags & AudioPacketFlags.HasPosition) != 0)
            {
                writer.Put(Position.X);
                writer.Put(Position.Y);
                writer.Put(Position.Z);
            }
            
            if ((Flags & AudioPacketFlags.HasRotation) != 0)
            {
                writer.Put(Rotation.X);
                writer.Put(Rotation.Y);
            }

            writer.Put(Data, 0, Length);
        }

        public void Deserialize(NetDataReader reader)
        {
            EntityId = reader.GetInt(); // Deserialize EntityId
            Timestamp = reader.GetUShort();
            FrameLoudness = Math.Clamp(reader.GetFloat(), 0f, 1f);
            Flags = (AudioPacketFlags)reader.GetByte();

            if ((Flags & AudioPacketFlags.HasPosition) != 0)
            {
                Position = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            }

            if ((Flags & AudioPacketFlags.HasRotation) != 0)
            {
                Rotation = new Vector2(reader.GetFloat(), reader.GetFloat());
            }

            Length = reader.AvailableBytes;
            if (Length > Constants.MaximumEncodedBytes)
                throw new InvalidOperationException($"Array length exceeds maximum number of bytes per packet! Got {Length} bytes.");
            
            Data = new byte[Length];
            reader.GetBytes(Data, Length);
        }

        public VcAdvancedAudioPacket Set(int entityId, ushort timestamp, float loudness, int length, byte[] data, Vector3? position = null, Vector2? rotation = null)
        {
            EntityId = entityId;
            Timestamp = timestamp;
            FrameLoudness = loudness;
            Length = length;
            Data = data;
            Flags = AudioPacketFlags.None;

            if (position.HasValue)
            {
                Position = position.Value;
                Flags |= AudioPacketFlags.HasPosition;
            }

            if (rotation.HasValue)
            {
                Rotation = rotation.Value;
                Flags |= AudioPacketFlags.HasRotation;
            }

            return this;
        }
    }
}
