﻿using BeardedManStudios.Forge.Networking;
using System.Net.Sockets;
using System;
using BeardedManStudios.Forge.Networking.Frame;
using System.IO;

public abstract class EBaseTCP : BaseTCP
{
    protected EBaseTCP(int maxConnections) : base(maxConnections) { }

    protected struct ReceiveToken
    {
        public NetworkingPlayer player;
        public int maxAllowedBytes;
        public int bytesReceived;
        public byte[] dataHolder;
        public ArraySegment<byte> internalBuffer;
    }

    private static readonly byte[] HEADER_DELIM = { 13, 10, 13, 10 }; // UTF-8 for \r\n\r\n i.e. CRLF CRLF, which appears after the last HTTP header field

    protected byte[] HandleHttpHeader(SocketAsyncEventArgs e, ref int bytesAlreadyProcessed)
    {
        if (bytesAlreadyProcessed < 0)
            throw new ArgumentException("bytesAlreadyProcessed must be non-negative.");
        else if (bytesAlreadyProcessed >= e.BytesTransferred)
            throw new ArgumentException("bytesAlreadyProcessed must be less than e.BytesTransferred.");

        ReceiveToken token = (ReceiveToken)e.UserToken;
        int totalBytes = token.bytesReceived + e.BytesTransferred - bytesAlreadyProcessed;
        if (totalBytes >= 4)
        {
            int searchSpace = totalBytes - 3; // totalBytes - HEADER_DELIM.Length + 1
            byte[] data = token.internalBuffer.Array;
            for (int i = 0; i < searchSpace; i++)
            {
                if (HEADER_DELIM[0] == data[token.internalBuffer.Offset + i])
                {
                    if (HEADER_DELIM[1] == data[token.internalBuffer.Offset + i + 1] &&
                        HEADER_DELIM[2] == data[token.internalBuffer.Offset + i + 2] &&
                        HEADER_DELIM[3] == data[token.internalBuffer.Offset + i + 3])
                    {
                        byte[] header = new byte[i + 4];
                        Buffer.BlockCopy(data, token.internalBuffer.Offset, header, 0, header.Length);
                        if (token.bytesReceived > 0)
                        {
                            bytesAlreadyProcessed += header.Length - token.bytesReceived;
                            token.bytesReceived = 0;
                            e.BufferList[0] = new ArraySegment<byte>(token.internalBuffer.Array, token.internalBuffer.Offset, token.internalBuffer.Count);
                        } else
                        {
                            bytesAlreadyProcessed += header.Length;
                        }
                        if (header.Length < totalBytes)
                        {
                            Buffer.BlockCopy(data, token.internalBuffer.Offset + header.Length, data, token.internalBuffer.Offset, totalBytes - header.Length);
                        }
                        return header;
                    }
                }
            }
            if (totalBytes == token.internalBuffer.Count)
                throw new InvalidDataException(String.Format("Could not find end of header within {0} bytes.", token.internalBuffer.Count));
        }

        token.bytesReceived = totalBytes;
        e.BufferList[0] = new ArraySegment<byte>(token.internalBuffer.Array, token.internalBuffer.Offset + totalBytes, token.internalBuffer.Count - totalBytes);
        bytesAlreadyProcessed = e.BytesTransferred;
        return null;
    }

    protected byte[] HandleData(SocketAsyncEventArgs e, bool isStream, ref int bytesAlreadyProcessed)
    {
        if (bytesAlreadyProcessed < 0)
            throw new ArgumentException("bytesAlreadyProcessed must be non-negative.");
        else if (bytesAlreadyProcessed >= e.BytesTransferred)
            throw new ArgumentException("bytesAlreadyProcessed must be less than e.BytesTransferred.");

        ReceiveToken token = (ReceiveToken)e.UserToken;
        int socketOffset = token.internalBuffer.Offset;
        byte[] bytes = token.internalBuffer.Array;
        int totalBytes;

        #region ParseFrameHeader
        if (token.dataHolder == null)
        {
            totalBytes = token.bytesReceived + e.BytesTransferred - bytesAlreadyProcessed;
            if (totalBytes < 2)
            {
                token.bytesReceived = totalBytes;
                e.BufferList[0] = new ArraySegment<byte>(token.internalBuffer.Array, socketOffset + totalBytes, token.internalBuffer.Count - totalBytes);
                bytesAlreadyProcessed = e.BytesTransferred;
                return null;
            }
            int dataLength = bytes[socketOffset + 1] & 127;
            bool usingMask = bytes[socketOffset + 1] > 127; // same as bytes[socketOffset + 1] & 128 != 0
            int payloadOffset;
            if (dataLength == 126)
            {
                payloadOffset = 4;
            } else if (dataLength == 127)
            {
                payloadOffset = 10;
            } else
            {
                payloadOffset = 2;
            }
            int length;
            if (payloadOffset != 2)
            {
                if (totalBytes < payloadOffset)
                {
                    token.bytesReceived = totalBytes;
                    e.BufferList[0] = new ArraySegment<byte>(token.internalBuffer.Array, socketOffset + totalBytes, token.internalBuffer.Count - totalBytes);
                    bytesAlreadyProcessed = e.BytesTransferred;
                    return null;
                }

                // Need to worry about endian order since length is in big endian
                if (payloadOffset == 4)
                {
                    if (BitConverter.IsLittleEndian)
                        length = BitConverter.ToUInt16(new byte[] { bytes[socketOffset + 3], bytes[socketOffset + 2] }, 0);
                    else
                        length = BitConverter.ToUInt16(bytes, socketOffset + 2);

                } else
                {
                    // First 4 bytes will be 0 for sizes less than max int32
                    if (BitConverter.IsLittleEndian)
                        length = (int)BitConverter.ToUInt32(new byte[] { bytes[socketOffset + 9], bytes[socketOffset + 8], bytes[socketOffset + 7], bytes[socketOffset + 6] }, 0);
                    else
                        length = (int)BitConverter.ToUInt32(bytes, socketOffset + 6);
                }
                if (isStream)
                    length += 21;  // Group id (4), receivers (1), time step (8), unique id (8)
                if ((bytes[socketOffset + 0] & 0xF) == (Binary.CONTROL_BYTE & 0xF))
                    length += 1; // routerId (1) if this is a binary frame
            } else
            {
                length = dataLength;
            }
            length += usingMask ? 4 + payloadOffset : payloadOffset;
            if (length < 0 || length > token.maxAllowedBytes)
                throw new UnauthorizedAccessException("Tried to receive a frame larger than expected.");
            e.BufferList[0] = token.internalBuffer;
            token.bytesReceived = 0;
            token.dataHolder = new byte[length];
        }
        #endregion

        totalBytes = token.bytesReceived + e.BytesTransferred - bytesAlreadyProcessed;
        if (totalBytes < token.dataHolder.Length)
        {
            Buffer.BlockCopy(bytes, socketOffset, token.dataHolder, token.bytesReceived, e.BytesTransferred - bytesAlreadyProcessed);
            token.bytesReceived += e.BytesTransferred - bytesAlreadyProcessed;
            bytesAlreadyProcessed = e.BytesTransferred;
            return null;
        } else
        {
            byte[] data = token.dataHolder;
            int dataProcessed = (data.Length - token.bytesReceived);
            Buffer.BlockCopy(bytes, socketOffset, data, token.bytesReceived, dataProcessed);


            token.bytesReceived = 0;
            token.dataHolder = null;
            bytesAlreadyProcessed += dataProcessed;
            if (bytesAlreadyProcessed < e.BytesTransferred)
            {
                Buffer.BlockCopy(bytes, socketOffset + dataProcessed, bytes, socketOffset, e.BytesTransferred - bytesAlreadyProcessed);
            }

            return data;
        }
    }

}
