using BrawlLib.Wii.Graphics;
using System;
using System.ComponentModel;

namespace BrawlLib.SSBB.ResourceNodes
{
    /// <summary>
    /// Editing of the three indirect texture matrices stored in a material.
    /// The matrices live in the material's indirect block (_indMtx) as raw BP register writes:
    /// matrix i is IND_MTXA/B/C(i), and each register holds two signed 1.10 fixed point numbers
    /// plus 2 bits of the shared 6 bit scale exponent. In the file, matrix 0 is stored in the first
    /// 32 byte display list together with the divide-by values, and matrices 1 and 2 in the second.
    /// </summary>
    public unsafe partial class MDL0MaterialNode
    {
        [Category("Indirect Texturing")]
        [DisplayName("Indirect Matrix 0")]
        [Description("Additional Notes: WARP / BUMP STRENGTH DIAL 0. Indirect texturing pushes each pixel of a texture sideways and up/down by an amount that depends on the colors of a second \"indirect\" texture (the warp or normal map). This matrix decides how strongly and in which direction that happens. It is used by TEV stages whose Matrix setting is Matrix0 (and by MatrixS0 / MatrixT0 for its scale). Expand it to edit the six numbers A-F and the Scale Exponent. A, C and E set the SIDEWAYS (S) push, using the indirect texture's s, t and u values. B, D and F set the UP/DOWN (T) push the same way. All zeros = no push. Changes show in the preview straight away. If the model plays an SRT0 animation that animates this matrix, the animation overrides these numbers while it plays.")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        public IndirectMatrixEditor IndirectMatrix0 => new IndirectMatrixEditor(this, 0);

        [Category("Indirect Texturing")]
        [DisplayName("Indirect Matrix 1")]
        [Description("Additional Notes: WARP / BUMP STRENGTH DIAL 1. Same as Indirect Matrix 0, but used by TEV stages whose Matrix setting is Matrix1 (or MatrixS1 / MatrixT1). Lets another layer of the material be pushed differently.")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        public IndirectMatrixEditor IndirectMatrix1 => new IndirectMatrixEditor(this, 1);

        [Category("Indirect Texturing")]
        [DisplayName("Indirect Matrix 2")]
        [Description("Additional Notes: WARP / BUMP STRENGTH DIAL 2. Same as Indirect Matrix 0, but used by TEV stages whose Matrix setting is Matrix2 (or MatrixS2 / MatrixT2).")]
        [TypeConverter(typeof(ExpandableObjectConverter))]
        public IndirectMatrixEditor IndirectMatrix2 => new IndirectMatrixEditor(this, 2);

        private void CopyIndBlock(int block, byte[] data, bool write)
        {
            switch (block)
            {
                case 0:
                    fixed (byte* p = _indMtx.Mtx0)
                    {
                        CopyIndBytes(p, data, write);
                    }

                    break;
                case 1:
                    fixed (byte* p = _indMtx.Mtx1)
                    {
                        CopyIndBytes(p, data, write);
                    }

                    break;
                default:
                    fixed (byte* p = _indMtx.Mtx2)
                    {
                        CopyIndBytes(p, data, write);
                    }

                    break;
            }
        }

        private static void CopyIndBytes(byte* p, byte[] data, bool write)
        {
            for (int i = 0; i < 15; i++)
            {
                if (write)
                {
                    p[i] = data[i];
                }
                else
                {
                    data[i] = p[i];
                }
            }
        }

        private static int SignExt11(uint v)
        {
            v &= 0x7FF;
            return (v & 0x400) != 0 ? (int) v - 0x800 : (int) v;
        }

        /// <summary>
        /// Reads indirect matrix i from the stored BP commands.
        /// v = A, B, C, D, E, F where S offset = A*s + C*t + E*u and T offset = B*s + D*t + F*u.
        /// blank is true if the material stores no commands for this matrix.
        /// </summary>
        internal void GetIndMatrix(int i, float[] v, out int exp, out bool blank)
        {
            uint[] w = new uint[3];
            blank = true;
            int baseReg = (int) BPMemory.BPMEM_IND_MTXA0 + i * 3;
            byte[] blk = new byte[15];
            for (int b = 0; b < 3; b++)
            {
                CopyIndBlock(b, blk, false);
                for (int k = 0; k < 3; k++)
                {
                    int reg = blk[k * 5 + 1] - baseReg;
                    if (blk[k * 5] == 0x61 && reg >= 0 && reg < 3)
                    {
                        w[reg] = (uint) ((blk[k * 5 + 2] << 16) | (blk[k * 5 + 3] << 8) | blk[k * 5 + 4]);
                        blank = false;
                    }
                }
            }

            v[0] = SignExt11(w[0]) / 1024.0f;
            v[1] = SignExt11(w[0] >> 11) / 1024.0f;
            v[2] = SignExt11(w[1]) / 1024.0f;
            v[3] = SignExt11(w[1] >> 11) / 1024.0f;
            v[4] = SignExt11(w[2]) / 1024.0f;
            v[5] = SignExt11(w[2] >> 11) / 1024.0f;
            int sBits = (int) (((w[0] >> 22) & 3) | (((w[1] >> 22) & 3) << 2) | (((w[2] >> 22) & 3) << 4));
            exp = sBits - 17;
        }

        internal void SetIndMatrix(int i, float[] v, int exp)
        {
            if (CheckIfMetal())
            {
                return;
            }

            int baseReg = (int) BPMemory.BPMEM_IND_MTXA0 + i * 3;
            byte[] blk = new byte[15];

            //Remove any copy of this matrix's commands stored in another block, so that
            //the one written below is the only one
            for (int b = 0; b < 3; b++)
            {
                if (b == i)
                {
                    continue;
                }

                CopyIndBlock(b, blk, false);
                bool changed = false;
                for (int k = 0; k < 3; k++)
                {
                    int reg = blk[k * 5 + 1] - baseReg;
                    if (blk[k * 5] == 0x61 && reg >= 0 && reg < 3)
                    {
                        for (int j = 0; j < 5; j++)
                        {
                            blk[k * 5 + j] = 0;
                        }

                        changed = true;
                    }
                }

                if (changed)
                {
                    CopyIndBlock(b, blk, true);
                }
            }

            int sBits = Math.Max(0, Math.Min(63, exp + 17));
            uint[] w = new uint[3];
            for (int r = 0; r < 3; r++)
            {
                int lo = Math.Max(-1024, Math.Min(1023, (int) Math.Round(v[r * 2] * 1024.0f)));
                int hi = Math.Max(-1024, Math.Min(1023, (int) Math.Round(v[r * 2 + 1] * 1024.0f)));
                w[r] = (uint) ((lo & 0x7FF) | ((hi & 0x7FF) << 11) | (((sBits >> (r * 2)) & 3) << 22));
            }

            //Rebuild matrix i's block from scratch: 3 BP commands (0x61, register, 24 bit value)
            blk = new byte[15];
            for (int r = 0; r < 3; r++)
            {
                blk[r * 5] = 0x61;
                blk[r * 5 + 1] = (byte) (baseReg + r);
                blk[r * 5 + 2] = (byte) (w[r] >> 16);
                blk[r * 5 + 3] = (byte) (w[r] >> 8);
                blk[r * 5 + 4] = (byte) w[r];
            }

            CopyIndBlock(i, blk, true);
            SignalPropertyChange();
        }
    }

    /// <summary>
    /// Property grid editor for one of a material's three indirect texture matrices.
    /// The indirect texture is read as three values (s, t, u). The push added to the texture's
    /// S (horizontal) and T (vertical) coordinate is:
    ///   S offset = A*s + C*t + E*u
    ///   T offset = B*s + D*t + F*u
    /// all multiplied by 2 to the power of the Scale Exponent.
    /// </summary>
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class IndirectMatrixEditor
    {
        private readonly MDL0MaterialNode _mat;
        private readonly int _idx;

        public IndirectMatrixEditor(MDL0MaterialNode mat, int idx)
        {
            _mat = mat;
            _idx = idx;
        }

        private float Get(int c)
        {
            float[] v = new float[6];
            _mat.GetIndMatrix(_idx, v, out _, out _);
            return v[c];
        }

        private void Set(int c, float value)
        {
            float[] v = new float[6];
            _mat.GetIndMatrix(_idx, v, out int exp, out bool blank);
            v[c] = value;
            _mat.SetIndMatrix(_idx, v, blank ? 0 : exp);
        }

        [DisplayName("A: S Offset")]
        [Description("Additional Notes: Matrix entry A. Pushes the picture SIDEWAYS (S, horizontal) using the indirect texture's FIRST value (s). 0 = no sideways push from s; positive and negative push opposite ways. A and D at about 0.5 with the other four at 0 is the usual simple warp. Range -1 to 0.999.")]
        public float A { get => Get(0); set => Set(0, value); }

        [DisplayName("B: T Offset")]
        [Description("Additional Notes: Matrix entry B. Pushes the picture UP/DOWN (T, vertical) using the indirect texture's FIRST value (s). 0 = none. Range -1 to 0.999.")]
        public float B { get => Get(1); set => Set(1, value); }

        [DisplayName("C: S Offset")]
        [Description("Additional Notes: Matrix entry C. Pushes the picture SIDEWAYS (S) using the indirect texture's SECOND value (t). Using both C and B makes the push swirl or slant instead of going straight along the axes. Range -1 to 0.999.")]
        public float C { get => Get(2); set => Set(2, value); }

        [DisplayName("D: T Offset")]
        [Description("Additional Notes: Matrix entry D. Pushes the picture UP/DOWN (T) using the indirect texture's SECOND value (t). Range -1 to 0.999.")]
        public float D { get => Get(3); set => Set(3, value); }

        [DisplayName("E: S Offset")]
        [Description("Additional Notes: Matrix entry E. Pushes the picture SIDEWAYS (S) using the indirect texture's THIRD value (u). Most warp textures only use s and t, so this is usually 0. Normal-map style setups can use u. Range -1 to 0.999.")]
        public float E { get => Get(4); set => Set(4, value); }

        [DisplayName("F: T Offset")]
        [Description("Additional Notes: Matrix entry F. Pushes the picture UP/DOWN (T) using the indirect texture's THIRD value (u). Usually 0. Range -1 to 0.999.")]
        public float F { get => Get(5); set => Set(5, value); }

        [DisplayName("Scale Exponent")]
        [Description("Additional Notes: MASTER VOLUME for the whole matrix. Each +1 doubles the push and each -1 halves it (range -17 to 46). Use this for big changes and A-F for fine tuning. A matrix with A and D at 0.5 and an exponent of 1 pushes with a strength of exactly 1.")]
        public int ScaleExponent
        {
            get
            {
                _mat.GetIndMatrix(_idx, new float[6], out int exp, out bool blank);
                return blank ? 0 : exp;
            }
            set
            {
                float[] v = new float[6];
                _mat.GetIndMatrix(_idx, v, out _, out _);
                _mat.SetIndMatrix(_idx, v, Math.Max(-17, Math.Min(46, value)));
            }
        }

        public override string ToString()
        {
            return "Matrix " + _idx;
        }
    }
}
