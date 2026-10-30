// Надёжные сообщения из protocol/src/main/proto/game.proto (ugame-server).
//
// Вместо пакета Google.Protobuf — минимальный кодировщик формата protobuf (proto3):
// сообщений всего семь, а лишняя DLL в Unity — лишние хлопоты со сборкой под мобильные платформы.
// Если game.proto сильно разрастётся, проще перейти на Google.Protobuf и сгенерированные классы.
// Неизвестные поля пропускаются, так что сервер может добавлять поля без поломки клиента.
using System;
using System.Collections.Generic;
using System.Text;

namespace UGame.Net
{
    public sealed class JoinRequest
    {
        public string Ticket = "";
        public string ClientVersion = "";

        public byte[] Encode()
        {
            var w = new ProtoWriter();
            w.String(1, Ticket);
            w.String(2, ClientVersion);
            return w.ToArray();
        }
    }

    public sealed class JoinResult
    {
        public bool Ok;
        public string Error = "";
        public uint EntityId, Team, TickRate;
        public string MatchId = "";

        public static JoinResult Decode(ProtoReader r)
        {
            var m = new JoinResult();
            while (r.Next(out int field, out int wire))
            {
                switch (field)
                {
                    case 1: m.Ok = r.Varint() != 0; break;
                    case 2: m.Error = r.String(); break;
                    case 3: m.EntityId = (uint)r.Varint(); break;
                    case 4: m.Team = (uint)r.Varint(); break;
                    case 5: m.TickRate = (uint)r.Varint(); break;
                    case 6: m.MatchId = r.String(); break;
                    default: r.Skip(wire); break;
                }
            }
            return m;
        }
    }

    public sealed class PlayerInfo
    {
        public uint EntityId;
        public long PlayerId;
        public string Username = "";
        public uint Team;

        public static PlayerInfo Decode(ProtoReader r)
        {
            var m = new PlayerInfo();
            while (r.Next(out int field, out int wire))
            {
                switch (field)
                {
                    case 1: m.EntityId = (uint)r.Varint(); break;
                    case 2: m.PlayerId = (long)r.Varint(); break;
                    case 3: m.Username = r.String(); break;
                    case 4: m.Team = (uint)r.Varint(); break;
                    default: r.Skip(wire); break;
                }
            }
            return m;
        }
    }

    public sealed class MatchStart
    {
        public string MatchId = "";
        public readonly List<PlayerInfo> Players = new List<PlayerInfo>();
        public uint DurationSec, StartTick;

        public static MatchStart Decode(ProtoReader r)
        {
            var m = new MatchStart();
            while (r.Next(out int field, out int wire))
            {
                switch (field)
                {
                    case 1: m.MatchId = r.String(); break;
                    case 2: m.Players.Add(PlayerInfo.Decode(r.Message())); break;
                    case 3: m.DurationSec = (uint)r.Varint(); break;
                    case 4: m.StartTick = (uint)r.Varint(); break;
                    default: r.Skip(wire); break;
                }
            }
            return m;
        }
    }

    public sealed class PlayerResult
    {
        public long PlayerId;
        public uint Team, Kills, Deaths;

        public static PlayerResult Decode(ProtoReader r)
        {
            var m = new PlayerResult();
            while (r.Next(out int field, out int wire))
            {
                switch (field)
                {
                    case 1: m.PlayerId = (long)r.Varint(); break;
                    case 2: m.Team = (uint)r.Varint(); break;
                    case 3: m.Kills = (uint)r.Varint(); break;
                    case 4: m.Deaths = (uint)r.Varint(); break;
                    default: r.Skip(wire); break;
                }
            }
            return m;
        }
    }

    public sealed class MatchEnd
    {
        public string MatchId = "";
        public int WinnerTeam; // -1 — ничья
        public readonly List<PlayerResult> Results = new List<PlayerResult>();

        public static MatchEnd Decode(ProtoReader r)
        {
            var m = new MatchEnd();
            while (r.Next(out int field, out int wire))
            {
                switch (field)
                {
                    case 1: m.MatchId = r.String(); break;
                    case 2: m.WinnerTeam = (int)r.Varint(); break; // int32: -1 приходит как 64-битный varint
                    case 3: m.Results.Add(PlayerResult.Decode(r.Message())); break;
                    default: r.Skip(wire); break;
                }
            }
            return m;
        }
    }

    public sealed class Kick
    {
        public string Reason = "";

        public static Kick Decode(ProtoReader r)
        {
            var m = new Kick();
            while (r.Next(out int field, out int wire))
            {
                if (field == 1) m.Reason = r.String();
                else r.Skip(wire);
            }
            return m;
        }
    }

    // ------------------------------------------------------------------ формат protobuf

    public sealed class ProtoWriter
    {
        readonly List<byte> buf = new List<byte>(64);

        public byte[] ToArray() => buf.ToArray();

        void RawVarint(ulong v)
        {
            while (v >= 0x80)
            {
                buf.Add((byte)(v | 0x80));
                v >>= 7;
            }
            buf.Add((byte)v);
        }

        public void String(int field, string value)
        {
            if (string.IsNullOrEmpty(value)) return; // proto3: пустая строка не пишется
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            RawVarint((ulong)((field << 3) | 2));
            RawVarint((ulong)bytes.Length);
            buf.AddRange(bytes);
        }
    }

    public sealed class ProtoReader
    {
        readonly byte[] buf;
        int pos;
        readonly int end;

        public ProtoReader(byte[] buf, int offset, int count)
        {
            this.buf = buf;
            pos = offset;
            end = offset + count;
        }

        public ProtoReader(ArraySegment<byte> seg) : this(seg.Array, seg.Offset, seg.Count) { }

        public bool Next(out int field, out int wire)
        {
            if (pos >= end)
            {
                field = wire = 0;
                return false;
            }
            ulong tag = Varint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong result = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (pos >= end) throw new FormatException("truncated varint");
                byte b = buf[pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new FormatException("varint too long");
        }

        int Length()
        {
            ulong len = Varint();
            if (len > (ulong)(end - pos)) throw new FormatException("bad length");
            return (int)len;
        }

        public string String()
        {
            int len = Length();
            string s = Encoding.UTF8.GetString(buf, pos, len);
            pos += len;
            return s;
        }

        public ProtoReader Message()
        {
            int len = Length();
            var r = new ProtoReader(buf, pos, len);
            pos += len;
            return r;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Advance(8); break;
                case 2: Advance(Length()); break;
                case 5: Advance(4); break;
                default: throw new FormatException("unsupported wire type " + wire);
            }
        }

        void Advance(int n)
        {
            if (n > end - pos) throw new FormatException("truncated");
            pos += n;
        }
    }
}
