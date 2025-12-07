using System;
using System.Text;

namespace VoiceCraft.Core
{
    /// <summary>
    /// Z85 encoding/decoding implementation based on ZeroMQ RFC 32.
    /// Converts binary data to/from printable ASCII text.
    /// Ratio: 4 bytes → 5 characters (25% size increase).
    /// </summary>
    public static class Z85
    {
        private const int Base85 = 85;
        private const int EncodingBlockSize = 4;
        private const int DecodedBlockSize = 5;

        private static readonly char[] EncodingTable = new char[]
        {
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
            'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
            'u', 'v', 'w', 'x', 'y', 'z', 'A', 'B', 'C', 'D',
            'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N',
            'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X',
            'Y', 'Z', '.', '-', ':', '+', '=', '^', '!', '/',
            '*', '?', '&', '<', '>', '(', ')', '[', ']', '{',
            '}', '@', '%', '$', '#'
        };

        // Decoding table: maps ASCII (32-127) to Z85 values (0-84)
        // Value 0xFF indicates invalid character
        private static readonly byte[] DecodingTable = new byte[]
        {
            0xFF, 0x44, 0xFF, 0x54, 0x53, 0x52, 0x48, 0xFF, // 32-39:  !"#$%&'
            0x4B, 0x4C, 0x46, 0x41, 0xFF, 0x3F, 0x3E, 0x45, // 40-47: ()*+,-./
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, // 48-55: 01234567
            0x08, 0x09, 0x40, 0xFF, 0x49, 0x42, 0x4A, 0x47, // 56-63: 89:;<=>?
            0x51, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, // 64-71: @ABCDEFG
            0x2B, 0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32, // 72-79: HIJKLMNO
            0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, // 80-87: PQRSTUVW
            0x3B, 0x3C, 0x3D, 0x4D, 0xFF, 0x4E, 0x43, 0xFF, // 88-95: XYZ[\]^_
            0xFF, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, // 96-103: `abcdefg
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, // 104-111: hijklmno
            0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, // 112-119: pqrstuvw
            0x21, 0x22, 0x23, 0x4F, 0xFF, 0x50, 0xFF, 0xFF  // 120-127: xyz{|}~DEL
        };

        /// <summary>
        /// Encodes a byte array into a Z85 string with automatic padding.
        /// </summary>
        /// <param name="data">The input byte array to encode.</param>
        /// <returns>The Z85 encoded string with padding indicator appended if needed.</returns>
        public static string GetStringWithPadding(Span<byte> data)
        {
            if (data.IsEmpty)
                return string.Empty;

            var lengthMod4 = data.Length % EncodingBlockSize;
            if (lengthMod4 == 0)
                return GetString(data);

            // Padding required
            var bytesToPad = EncodingBlockSize - lengthMod4;
            var paddedData = new byte[data.Length + bytesToPad];
            data.CopyTo(paddedData);
            // Remaining bytes are already zero-initialized

            return GetString(paddedData) + bytesToPad.ToString();
        }

        /// <summary>
        /// Encodes a byte array into a Z85 string. The input length must be a multiple of 4.
        /// </summary>
        /// <param name="data">The input byte array to encode.</param>
        /// <returns>The Z85 encoded string.</returns>
        /// <exception cref="ArgumentException">Thrown when the input length is not a multiple of 4.</exception>
        public static string GetString(Span<byte> data)
        {
            if (data.Length % EncodingBlockSize != 0)
                throw new ArgumentException(
                    $"Input length must be a multiple of {EncodingBlockSize}. Got {data.Length}.", 
                    nameof(data));

            if (data.IsEmpty)
                return string.Empty;

            var outputLength = data.Length / EncodingBlockSize * DecodedBlockSize;
            var stringBuilder = new StringBuilder(outputLength);
            var encodedChars = new char[DecodedBlockSize];

            for (var i = 0; i < data.Length; i += EncodingBlockSize)
            {
                // Combine 4 bytes into a 32-bit unsigned integer (big-endian)
                var value = (uint)((data[i] << 24) |
                                   (data[i + 1] << 16) |
                                   (data[i + 2] << 8) |
                                   data[i + 3]);

                // Convert to 5 base-85 digits
                encodedChars[4] = EncodingTable[(int)(value % Base85)];
                value /= Base85;
                encodedChars[3] = EncodingTable[(int)(value % Base85)];
                value /= Base85;
                encodedChars[2] = EncodingTable[(int)(value % Base85)];
                value /= Base85;
                encodedChars[1] = EncodingTable[(int)(value % Base85)];
                value /= Base85;
                encodedChars[0] = EncodingTable[(int)value];

                stringBuilder.Append(encodedChars);
            }

            return stringBuilder.ToString();
        }

        /// <summary>
        /// Decodes a Z85 string with padding into a byte array.
        /// </summary>
        /// <param name="data">The input Z85 string with optional padding indicator.</param>
        /// <returns>The decoded byte array.</returns>
        /// <exception cref="ArgumentException">Thrown when the input is invalid.</exception>
        /// <exception cref="ArgumentNullException">Thrown when input is null.</exception>
        public static byte[] GetBytesWithPadding(string data)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length == 0)
                return Array.Empty<byte>();

            var lengthMod5 = data.Length % DecodedBlockSize;
            
            // Check if padding indicator is present
            if (lengthMod5 == 0)
                return GetBytes(data);

            // Should have exactly 1 character padding indicator
            if (lengthMod5 != 1)
                throw new ArgumentException(
                    $"Invalid Z85 string length. Expected multiple of {DecodedBlockSize} or multiple of {DecodedBlockSize} + 1 for padding.", 
                    nameof(data));

            var paddingChar = data[data.Length - 1];
            if (paddingChar < '1' || paddingChar > '3')
                throw new ArgumentException(
                    $"Invalid padding indicator '{paddingChar}'. Must be '1', '2', or '3'.", 
                    nameof(data));

            var paddedBytes = paddingChar - '0';
            var encodedData = data.Substring(0, data.Length - 1);
            var output = GetBytes(encodedData);

            // Remove padded bytes by returning a smaller array
            var resultLength = output.Length - paddedBytes;
            if (resultLength < 0)
                throw new ArgumentException("Padding indicator exceeds decoded data length.", nameof(data));

            var result = new byte[resultLength];
            Array.Copy(output, 0, result, 0, resultLength);
            return result;
        }

        /// <summary>
        /// Decodes a Z85 string into a byte array. The input length must be a multiple of 5.
        /// </summary>
        /// <param name="data">The input Z85 string to decode.</param>
        /// <returns>The decoded byte array.</returns>
        /// <exception cref="ArgumentException">Thrown when the input length is not a multiple of 5 or contains invalid characters.</exception>
        public static byte[] GetBytes(string data)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));
                
            if (data.Length % DecodedBlockSize != 0)
                throw new ArgumentException(
                    $"Input length must be a multiple of {DecodedBlockSize}. Got {data.Length}.", 
                    nameof(data));

            if (data.Length == 0)
                return Array.Empty<byte>();

            var output = new byte[data.Length / DecodedBlockSize * EncodingBlockSize];
            var outputIndex = 0;

            for (var i = 0; i < data.Length; i += DecodedBlockSize)
            {
                uint value = 0;
                
                for (var j = 0; j < DecodedBlockSize; j++)
                {
                    var c = data[i + j];
                    
                    // Validate character range
                    if (c < 32 || c > 127)
                        throw new ArgumentException(
                            $"Invalid character '{c}' (0x{(int)c:X2}) at position {i + j}. Character must be ASCII 32-127.", 
                            nameof(data));

                    var decoded = DecodingTable[c - 32];
                    
                    // Check for invalid character in decoding table
                    if (decoded == 0xFF)
                        throw new ArgumentException(
                            $"Invalid Z85 character '{c}' at position {i + j}.", 
                            nameof(data));

                    value = value * Base85 + decoded;
                }

                // Extract 4 bytes from the 32-bit value (big-endian)
                output[outputIndex] = (byte)(value >> 24);
                output[outputIndex + 1] = (byte)(value >> 16);
                output[outputIndex + 2] = (byte)(value >> 8);
                output[outputIndex + 3] = (byte)value;
                outputIndex += EncodingBlockSize;
            }

            return output;
        }
    }
}

