using System;
using PurrNet.Packing;

namespace Frognet.Data
{
    /// <summary>
    /// Puts a <see cref="Record"/> on the wire as numbers: a leaf id and only the arguments that
    /// carry something. PurrNet's codegen picks these up by signature, nothing has to register them.
    /// </summary>
    /// <remarks>
    /// Each value is self describing, so a peer running different files produces garbage values
    /// rather than a stream that desyncs everything after it. Compare
    /// <see cref="DataRegistry.Hash"/> on connect to catch that case properly.
    /// </remarks>
    public static class RecordPacking
    {
        private const int NumericMask = 0b0000_0111;
        private const int TextFlag = 0b0000_1000;

        public static void Write(this BitPacker packer, Record record)
        {
            int count = Math.Min(record.Count, byte.MaxValue);
            packer.Write((byte)count);

            for (int i = 0; i < count; i++)
            {
                DataValue value = record.values[i];
                int numeric = NumericCount(value);
                int header = numeric;

                if (value.text != null)
                    header |= TextFlag;

                packer.Write((ushort)Math.Max(0, Math.Min(value.leaf, ushort.MaxValue)));
                packer.Write((byte)header);

                for (int a = 0; a < numeric; a++)
                {
                    float number = value[a];
                    packer.Write(number);
                }

                if (value.text != null)
                    packer.Write(value.text);
            }
        }

        public static void Read(this BitPacker packer, ref Record record)
        {
            byte count = 0;
            packer.Read(ref count);

            if (count == 0)
            {
                record = default;
                return;
            }

            var values = new DataValue[count];
            bool sorted = true;

            for (int i = 0; i < count; i++)
            {
                ushort leaf = 0;
                packer.Read(ref leaf);

                byte header = 0;
                packer.Read(ref header);

                var value = new DataValue { leaf = leaf };
                int numeric = header & NumericMask;

                for (int a = 0; a < numeric; a++)
                {
                    float number = 0f;
                    packer.Read(ref number);
                    value = value.WithArg(a, number);
                }

                if ((header & TextFlag) != 0)
                {
                    string text = null;
                    packer.Read(ref text);
                    value.text = text;
                }

                values[i] = value;

                if (i > 0 && values[i].leaf <= values[i - 1].leaf)
                    sorted = false;
            }

            record = sorted ? new Record { values = values } : Rebuild(values);
        }

        /// <summary>
        /// How many argument slots actually carry something. Slots fill from the first, so trailing
        /// zeroes can be dropped and still read back identically.
        /// </summary>
        private static int NumericCount(DataValue value)
        {
            if (value.w != 0f) return 4;
            if (value.z != 0f) return 3;
            if (value.y != 0f) return 2;
            if (value.x != 0f) return 1;
            return 0;
        }

        /// <summary>Restores the sorted-by-leaf invariant when a peer sends values out of order.</summary>
        private static Record Rebuild(DataValue[] values)
        {
            DataValue[] result = null;

            for (int i = 0; i < values.Length; i++)
                result = Record.Set(result, values[i], false);

            return new Record { values = result };
        }
    }
}
