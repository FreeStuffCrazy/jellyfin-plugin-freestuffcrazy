using System.Buffers.Binary;

namespace Jellyfin.Plugin.FreeStuffCrazy.Relay;

/// <summary>Frame types; see PROTOCOL.md.</summary>
internal enum FrameType : byte
{
    Control = 0,
    Open = 1,
    Headers = 2,
    Data = 3,
    End = 4,
    Window = 5,
    Cancel = 6,
    Error = 7,
}

/// <summary>One protocol frame: a type, a stream ID and a payload.</summary>
internal readonly record struct Frame(FrameType Type, uint Stream, ReadOnlyMemory<byte> Payload)
{
    public const int HeaderLength = 5;

    /// <summary>The most body bytes a single DATA frame carries.</summary>
    public const int MaxDataLength = 64 * 1024;

    public static byte[] Encode(FrameType type, uint stream, ReadOnlySpan<byte> payload = default)
    {
        var bytes = new byte[HeaderLength + payload.Length];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1, 4), stream);
        payload.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    public static byte[] EncodeWindow(uint stream, uint bytes)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, bytes);
        return Encode(FrameType.Window, stream, payload);
    }

    /// <summary>Parses one WebSocket message; false if it is too short or of an unknown type.</summary>
    public static bool TryDecode(ReadOnlyMemory<byte> message, out Frame frame)
    {
        frame = default;
        if (message.Length < HeaderLength || message.Span[0] > (byte)FrameType.Error)
        {
            return false;
        }

        frame = new Frame(
            (FrameType)message.Span[0],
            BinaryPrimitives.ReadUInt32BigEndian(message.Span.Slice(1, 4)),
            message[HeaderLength..]);
        return true;
    }

    /// <summary>Reads a WINDOW frame's byte count; 0 if the payload is malformed.</summary>
    public uint WindowBytes() =>
        Payload.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(Payload.Span) : 0;
}
