// Зеркало модуля protocol из ugame-server (Java). Порядок байтов — little-endian.
// Изменили формат на сервере — меняйте и здесь.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UGame.Net
{
    public static class MessageType
    {
        // надёжный канал, protobuf (game.proto)
        public const byte C2S_Join = 1;
        public const byte S2C_JoinResult = 2;
        public const byte S2C_MatchStart = 3;
        public const byte S2C_MatchEnd = 4;
        public const byte S2C_Kick = 5;

        // ненадёжный канал, ручная упаковка
        public const byte C2S_Input = 20;
        public const byte S2C_Snapshot = 21;
    }

    public sealed class ByteWriter
    {
        byte[] buf;
        int pos;

        public ByteWriter(int capacity = 256) { buf = new byte[capacity]; }

        public int Size => pos;
        public ArraySegment<byte> Segment => new ArraySegment<byte>(buf, 0, pos);

        public ByteWriter Reset() { pos = 0; return this; }

        void Ensure(int extra)
        {
            if (pos + extra > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, pos + extra));
        }

        public ByteWriter U8(int v) { Ensure(1); buf[pos++] = (byte)v; return this; }
        public ByteWriter I8(int v) => U8((sbyte)v);
        public ByteWriter U16(int v) { Ensure(2); buf[pos++] = (byte)v; buf[pos++] = (byte)(v >> 8); return this; }

        public ByteWriter Bytes(byte[] src, int offset, int count)
        {
            Ensure(count);
            Buffer.BlockCopy(src, offset, buf, pos, count);
            pos += count;
            return this;
        }
    }

    public sealed class ByteReader
    {
        readonly byte[] buf;
        int pos;
        readonly int end;

        public ByteReader(ArraySegment<byte> seg) { buf = seg.Array; pos = seg.Offset; end = seg.Offset + seg.Count; }

        void Need(int n) { if (end - pos < n) throw new FormatException("packet too short"); }

        public int U8() { Need(1); return buf[pos++]; }
        public int U16() { Need(2); int v = buf[pos] | (buf[pos + 1] << 8); pos += 2; return v; }
        public int I16() => (short)U16();
        public uint U32()
        {
            Need(4);
            uint v = (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24));
            pos += 4;
            return v;
        }
    }

    public static class Quantize
    {
        public static int AxisToI8(float a) => Mathf.RoundToInt(Mathf.Clamp(a, -1f, 1f) * 127f);
        public static float I8ToAxis(int v) => Mathf.Clamp(v / 127f, -1f, 1f);
        public static float I16ToPos(int v) => v / 100f;

        public static int AngleToU16(float rad)
        {
            double twoPi = Math.PI * 2;
            double a = rad % twoPi;
            if (a < 0) a += twoPi;
            return (int)Math.Round(a / twoPi * 65536.0) & 0xFFFF;
        }

        public static float U16ToAngle(int v) => (float)((v & 0xFFFF) / 65536.0 * Math.PI * 2);

        /// <summary>16-битные номера с переполнением: true, если a «новее» b.</summary>
        public static bool SeqGreater(int a, int b)
        {
            int d = (a - b) & 0xFFFF;
            return d != 0 && d < 0x8000;
        }
    }

    [Flags]
    public enum Buttons : byte { None = 0, Attack = 1, Super = 2 }

    public struct InputCommand
    {
        public ushort Seq;
        public sbyte MoveX, MoveY;   // уже квантованные оси [-127..127] — их же использует предсказание
        public ushort Aim;           // 0..65535 = 0..2π
        public Buttons Buttons;
    }

    public static class InputPacket
    {
        public const int MaxCommands = 4;

        /// <param name="newestFirst">команды от НОВОЙ к старой</param>
        public static void Write(ByteWriter w, IReadOnlyList<InputCommand> newestFirst)
        {
            int count = Math.Min(newestFirst.Count, MaxCommands);
            w.U8(MessageType.C2S_Input).U8(count);
            for (int i = 0; i < count; i++)
            {
                var c = newestFirst[i];
                w.U16(c.Seq).I8(c.MoveX).I8(c.MoveY).U16(c.Aim).U8((byte)c.Buttons);
            }
        }
    }

    public enum EntityKind : byte { Brawler = 1, Projectile = 2 }

    public struct EntityState
    {
        public ushort Id;
        public EntityKind Kind;
        public Vector2 Pos;   // метры, координаты арены
        public float Aim;     // радианы
        public float Hp01;    // 0..1, 0 — мёртв
        public byte Team;
    }

    public sealed class Snapshot
    {
        public uint Tick;
        public ushort AckInputSeq;
        public ushort TimeLeftSec;
        public byte Score0, Score1;
        public readonly List<EntityState> Entities = new List<EntityState>();

        /// <summary>Читает пакет БЕЗ первого байта типа.</summary>
        public static Snapshot ReadBody(ByteReader r)
        {
            var s = new Snapshot
            {
                Tick = r.U32(),
                AckInputSeq = (ushort)r.U16(),
                TimeLeftSec = (ushort)r.U16(),
                Score0 = (byte)r.U8(),
                Score1 = (byte)r.U8(),
            };
            int count = r.U8();
            for (int i = 0; i < count; i++)
            {
                s.Entities.Add(new EntityState
                {
                    Id = (ushort)r.U16(),
                    Kind = (EntityKind)r.U8(),
                    Pos = new Vector2(Quantize.I16ToPos(r.I16()), Quantize.I16ToPos(r.I16())),
                    Aim = Quantize.U16ToAngle(r.U16()),
                    Hp01 = r.U8() / 255f,
                    Team = (byte)r.U8(),
                });
            }
            return s;
        }
    }
}
