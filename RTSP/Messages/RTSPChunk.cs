namespace Rtsp.Messages;

using System;

/// <summary>
/// Class which represent each message exchanged on Rtsp socket.
/// </summary>
public abstract class RtspChunk : ICloneable
{
    /// <summary>
    /// Gets or sets the data associate with the message.
    /// </summary>
    /// <value>Array of byte transmit with the message.</value>
    public virtual Memory<byte> Data { get; set; } = Memory<byte>.Empty;

    /// <summary>
    /// Gets or sets the source port which receive the message.
    /// </summary>
    /// <value>The source port.</value>
    public RtspListener? SourcePort { get; set; }

    public abstract object Clone();
}