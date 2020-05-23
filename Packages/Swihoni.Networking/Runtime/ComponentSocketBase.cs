using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Swihoni.Collections;
using Swihoni.Components;
using UnityEngine;

namespace Swihoni.Networking
{
    public abstract class ComponentSocketBase : IDisposable
    {
        private const int BufferSize = 1 << 16;

        protected readonly IPEndPoint m_Ip;
        protected readonly Socket m_RawSocket;
        protected readonly HashSet<IPEndPoint> m_Connections = new HashSet<IPEndPoint>();

        private readonly DualDictionary<Type, byte> m_Codes;
        private readonly Dictionary<Type, Pool<NetMessageComponent>> m_MessagePools = new Dictionary<Type, Pool<NetMessageComponent>>();
        private readonly float m_StartTime;
        private EndPoint m_ReceiveEndPoint = new IPEndPoint(IPAddress.Any, 0);
        private long m_BytesSent, m_BytesReceived;

        public float SendRate => m_BytesSent / (Time.realtimeSinceStartup - m_StartTime) * 0.001f;
        public float ReceiveRateS => m_BytesReceived / (Time.realtimeSinceStartup - m_StartTime) * 0.001f;
        public HashSet<IPEndPoint> Connections => m_Connections;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected ComponentSocketBase(IPEndPoint ip, )
        {
            m_Ip = ip;
            m_Codes = new DualDictionary<Type, byte>();
            m_RawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            m_StartTime = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Register element type for serialization over network.
        /// Assigns an ID to each type. Order of registration is important.
        /// Container types must have an instance passed to figure out its children elements.
        /// The order of those element types is also important.
        /// </summary>
        public void RegisterSimpleElement(Type registerType)
        {
            m_Codes.Add(registerType, (byte) m_Codes.Length);
            var types = new [] {}
            m_MessagePools[registerType] = new Pool<NetMessageComponent>(1, () => new NetMessageComponent(registerType));
        }

        public void RegisterContainer(Type registerType, Container example)
        {
            m_Codes.Add(registerType, (byte) m_Codes.Length);
            m_MessagePools[registerType] = new Pool<NetMessageComponent>(1, () => new NetMessageComponent(example.ElementTypes));
        }

        public void PollReceived(Action<IPEndPoint, ElementBase> received)
        {
            while (m_RawSocket.Available > 0)
            {
                // TODO:performance use bytes receive
                try
                {
                    int bytesReceived = m_RawSocket.ReceiveFrom(m_ReadStream.GetBuffer(), 0, BufferSize, SocketFlags.None, ref m_ReceiveEndPoint);
                    m_BytesReceived += bytesReceived;
                    if (!(m_ReceiveEndPoint is IPEndPoint ipEndPoint)) continue;
                    bool isNewConnection = !m_Connections.Contains(ipEndPoint);
                    if (isNewConnection)
                        m_Connections.Add(new IPEndPoint(ipEndPoint.Address, ipEndPoint.Port));
                    m_ReadStream.Position = 0;
                    byte code = m_Reader.ReadByte();
                    Type type = m_Codes.GetReverse(code);
                    ElementBase message = m_MessagePools[type].Obtain();
                    message.Reset();
                    message.Deserialize(m_ReadStream);
                    received(ipEndPoint, message);
                    m_MessagePools[message.GetType()].Return(message);
                }
                catch (SocketException)
                {
                    // TODO:safety handle
                }
                catch (Exception exception)
                {
                    Debug.LogError(exception);
                    throw;
                }
            }
        }

        public bool Send(NetMessageComponent message, IPEndPoint endPoint)
        {
            try
            {
                byte code = m_Codes.GetForward(message.GetType());
                m_SendStream.Position = 0;
                m_Writer.Write(code);
                message.Serialize(m_SendStream);
                // var e = new SocketAsyncEventArgs();
                // e.RemoteEndPoint = endPoint;
                // e.SetBuffer(m_SendStream.GetBuffer(), 0, (int) m_SendStream.Position + 1);
                // m_RawSocket.SendToAsync()
                int sent = m_RawSocket.SendTo(m_SendStream.GetBuffer(), 0, (int) m_SendStream.Position + 1, SocketFlags.None, endPoint);
                m_BytesSent += sent;
                return true;
            }
            catch (KeyNotFoundException keyNotFoundException)
            {
                throw new Exception("Type has not been registered to send across socket!", keyNotFoundException);
            }
            catch (Exception exception)
            {
                Debug.LogError(exception);
                return false;
            }
        }

        public void Dispose()
        {
            m_RawSocket.Dispose();
            m_SendStream.Dispose();
            m_Writer.Dispose();
        }
    }
}