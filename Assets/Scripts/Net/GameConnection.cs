// Подключение к игровому серверу через kcp2k (UDP).
// Не MonoBehaviour: GameClient вызывает Tick()/Flush() каждый кадр.
using System;
using System.Collections.Generic;
using kcp2k;
using UnityEngine;

namespace UGame.Net
{
    public sealed class GameConnection
    {
        // Должно совпадать с kcp.* в game-server.properties
        static KcpConfig Config() => new KcpConfig(
            DualMode: false,
            RecvBufferSize: 1024 * 1024,
            SendBufferSize: 1024 * 1024,
            Mtu: 1200,
            NoDelay: true,
            Interval: 10,
            FastResend: 0,
            CongestionWindow: false,
            SendWindowSize: 32,
            ReceiveWindowSize: 128,
            Timeout: 10000);

        public event Action<JoinResult> JoinResultReceived;
        public event Action<MatchStart> MatchStarted;
        public event Action<MatchEnd> MatchEnded;
        public event Action<Snapshot> SnapshotReceived;
        public event Action<string> Kicked;
        /// <summary>Соединение закрыто не нами. Аргумент — причина для показа игроку.</summary>
        public event Action<string> Disconnected;

        readonly KcpClient client;
        readonly ByteWriter writer = new ByteWriter(64);
        string ticket;
        string lastError;
        bool closedByUs;

        static bool logHooked;

        public GameConnection()
        {
            if (!logHooked)
            {
                kcp2k.Log.Info = _ => { }; // kcp2k очень разговорчив на уровне Info
                kcp2k.Log.Warning = Debug.LogWarning;
                kcp2k.Log.Error = Debug.LogError;
                logHooked = true;
            }
            client = new KcpClient(OnConnected, OnData, OnDisconnected, OnError, Config());
        }

        public bool IsConnected => client.connected;

        public void Connect(string host, int port, string matchTicket)
        {
            ticket = matchTicket;
            // localhost в Windows часто резолвится в ::1, а сервер слушает IPv4
            if (host == "localhost") host = "127.0.0.1";
            client.Connect(host, (ushort)port);
        }

        public void Disconnect()
        {
            closedByUs = true;
            client.Disconnect();
        }

        /// <summary>В начале кадра: принять пакеты и вызвать события.</summary>
        public void Tick() => client.TickIncoming();

        /// <summary>В конце кадра: отправить всё накопленное.</summary>
        public void Flush() => client.TickOutgoing();

        public void SendInputs(IReadOnlyList<InputCommand> newestFirst)
        {
            if (!client.connected) return;
            writer.Reset();
            InputPacket.Write(writer, newestFirst);
            client.Send(writer.Segment, KcpChannel.Unreliable);
        }

        void OnConnected()
        {
            // рукопожатие kcp2k пройдено — представляемся билетом из матчмейкинга
            byte[] body = new JoinRequest { Ticket = ticket, ClientVersion = Application.version }.Encode();
            writer.Reset().U8(MessageType.C2S_Join).Bytes(body, 0, body.Length);
            client.Send(writer.Segment, KcpChannel.Reliable);
        }

        void OnData(ArraySegment<byte> data, KcpChannel channel)
        {
            if (data.Count < 1) return;
            byte type = data.Array[data.Offset];
            var body = new ArraySegment<byte>(data.Array, data.Offset + 1, data.Count - 1);
            try
            {
                switch (type)
                {
                    case MessageType.S2C_Snapshot:
                        SnapshotReceived?.Invoke(Snapshot.ReadBody(new ByteReader(body)));
                        break;
                    case MessageType.S2C_JoinResult:
                        JoinResultReceived?.Invoke(JoinResult.Decode(new ProtoReader(body)));
                        break;
                    case MessageType.S2C_MatchStart:
                        MatchStarted?.Invoke(MatchStart.Decode(new ProtoReader(body)));
                        break;
                    case MessageType.S2C_MatchEnd:
                        MatchEnded?.Invoke(MatchEnd.Decode(new ProtoReader(body)));
                        break;
                    case MessageType.S2C_Kick:
                        Kicked?.Invoke(Kick.Decode(new ProtoReader(body)).Reason);
                        break;
                }
            }
            catch (FormatException e)
            {
                Debug.LogWarning($"Битое сообщение типа {type}: {e.Message}");
            }
        }

        void OnError(ErrorCode code, string message)
        {
            lastError = code.ToString();
            Debug.LogWarning($"KCP {code}: {message}");
        }

        void OnDisconnected()
        {
            if (closedByUs) return;
            Disconnected?.Invoke(lastError ?? "сервер закрыл соединение");
        }
    }
}
