using BrawlLib.SSBB.ResourceNodes;
using BrawlLib.SSBB.Types;
using OpenTK.Graphics.OpenGL;
using System;

namespace BrawlLib.Wii.Graphics
{
    /// <summary>
    /// Indirect texturing support for the preview shaders:
    /// - Warp / distortion (indirect texture offsets, 8/5/4/3 bit formats, bias, matrices 0-2,
    ///   dynamic S/T matrices, wrapping, "add previous stage").
    /// - Bump alpha (the "BumpAlpha"/"NormalizedBumpAlpha" raster inputs).
    /// - Normal map bump mapping, which the game builds from the "Emboss Map" texture coordinate
    ///   generator (the light direction in tangent space is added to the texture coordinate)
    ///   combined with the dynamic S/T indirect matrices.
    /// All of this only generates code for materials that use indirect texturing or an emboss
    /// texture coordinate. Every other material gets exactly the same shader as before.
    /// Based on the GX hardware behavior as implemented by noclip.website (gx_material.ts).
    /// </summary>
    public unsafe partial class ShaderGenerator
    {
        //Uniforms
        //indMtxA/indMtxB hold the 3 indirect matrices as two rows of (m0, m1, m2, w).
        //For row A, w is the matrix scale (2^exponent). Row B's w is unused.
        private const string _uIndMtxAName = "indMtxA";
        private const string _uIndMtxBName = "indMtxB";

        //Per indirect stage 1/divide-by value for the S and T axes
        private const string _uIndScaleName = "indScale";

        //Number of indirect stages the material actually has active
        private const string _uIndCountName = "indStageCount";

        //Size in pixels of the texture bound to each map, used to convert normalized
        //coordinates to texel space for indirect offsets
        private const string _uTexSizeName = "texSize";

        //Running TEV texture coordinate (texels), used by "add previous stage"
        private const string TevCoordName = "tevCoord";

        //Emboss (normal map) texture coordinates
        private const string EmbossCoordName = "emb";

        //Tracks which TEV stage the current indirect lookup (indRaw/indCrd) was written for,
        //so that a stage using it for both its texture and its bump alpha only declares it once
        private static MDL0TEVStageNode _indLookupStage;
        private static bool _indLookupOk;

        #region Queries

        private static bool UsesIndirect => _material != null && _material._activeIndStages > 0;

        private static bool IsEmboss(int coordID)
        {
            return _material != null && coordID >= 0 && coordID < _material.Children.Count &&
                   _material.Children[coordID] is MDL0MaterialRefNode mr &&
                   mr.Type == TexTexgenType.EmbossMap;
        }

        private static bool UsesEmboss()
        {
            for (int i = 0; i < _material.Children.Count; i++)
            {
                if (IsEmboss(i))
                {
                    return true;
                }
            }

            return false;
        }

        //Texture coordinate as it comes out of the vertex shader
        private static string RawUv(int coordID)
        {
            return UsePixelLighting ? $"uv{coordID}" : $"gl_TexCoord[{coordID}]";
        }

        //Texture coordinate used by TEV stages (the emboss version for emboss texture coordinates)
        private static string StageUv(int coordID)
        {
            return IsEmboss(coordID) ? EmbossCoordName + coordID : RawUv(coordID);
        }

        #endregion

        #region Shader declarations

        /// <summary>
        /// True if the fragment shader needs the light uniforms even though it isn't
        /// doing pixel lighting (emboss texture coordinates are calculated per pixel).
        /// </summary>
        private static bool NeedsEmbossLights()
        {
            return !UsePixelLighting && UsesEmboss();
        }

        private static void WriteIndirectFragmentUniforms()
        {
            if (UsesIndirect)
            {
                wU("int {0};", _uIndCountName);
                wU("vec4 {0}[3];", _uIndMtxAName);
                wU("vec4 {0}[3];", _uIndMtxBName);
                wU("vec2 {0}[4];", _uIndScaleName);
                wU("vec2 {0}[8];", _uTexSizeName);
                wl();
            }

            if (NeedsEmbossLights())
            {
                wU("{0} {1}[8];", LightStructName, _uSCNLightSetLightsName);
                wl();
            }
        }

        /// <summary>
        /// Written at the start of the fragment shader's main function.
        /// </summary>
        private static void WriteIndirectPrelude()
        {
            _indLookupStage = null;
            _indLookupOk = false;

            if (UsesIndirect)
            {
                Comment("Running texture coordinate (in texels) for indirect texturing");
                wl("vec2 {0} = vec2(0.0);", TevCoordName);
                wl();
            }

            for (int i = 0; i < _material.Children.Count; i++)
            {
                if (IsEmboss(i))
                {
                    WriteEmbossCoord((MDL0MaterialRefNode) _material.Children[i], i);
                }
            }
        }

        /// <summary>
        /// Emboss Map texture coordinate generation, which is how normal map bump mapping works.
        /// The new coordinate is the source coordinate plus the direction to the light expressed along
        /// the surface's tangent and binormal: uv + (dot(L, tangent), dot(L, binormal)).
        /// The game stores tangent/binormal per vertex, which the preview doesn't load, so they are
        /// rebuilt here from how the source UVs lay out over the surface.
        /// </summary>
        private static void WriteEmbossCoord(MDL0MaterialRefNode mr, int i)
        {
            //The source is another texture coordinate that comes before this one
            int src = mr.EmbossSource >= 0 && mr.EmbossSource < i ? mr.EmbossSource : i;
            int light = Math.Max(0, Math.Min(7, mr.EmbossLight));
            string e = EmbossCoordName + i;

            Comment("Emboss map (normal map) texture coordinate " + i + ", source " + src + ", light " + light);
            wl("vec4 {0} = {1};", e, RawUv(i));
            OpenBracket();
            {
                wl("vec2 baseUV = {0}.st;", RawUv(src));
                wl("vec3 nrm = normalize({0});", _vNormalName);
                wl("vec3 dp1 = dFdx({0});", _vPositionName);
                wl("vec3 dp2 = dFdy({0});", _vPositionName);
                wl("vec2 duv1 = dFdx(baseUV);");
                wl("vec2 duv2 = dFdy(baseUV);");
                wl("vec3 dp2perp = cross(dp2, nrm);");
                wl("vec3 dp1perp = cross(nrm, dp1);");
                wl("vec3 tng = dp2perp * duv1.x + dp1perp * duv2.x;");
                wl("vec3 bnm = dp2perp * duv1.y + dp1perp * duv2.y;");
                wl("float tbScale = inversesqrt(max(max(dot(tng, tng), dot(bnm, bnm)), 1e-12));");
                wl("tng *= tbScale;");
                wl("bnm *= tbScale;");

                //Use the requested light. If it is off, use the first light. If that is off too,
                //use a fixed direction so the bumps are still visible.
                wl("{0} embLight = {1}[{2}];", LightStructName, _uSCNLightSetLightsName, light.ToString());
                wl("if (embLight.{0} == 0)", LightEnabledName);
                OpenBracket();
                wl("embLight = {0}[0];", _uSCNLightSetLightsName);
                CloseBracket();
                wl("vec3 embDir = normalize(vec3(0.35, 0.55, 0.75));");
                wl("if (embLight.{0} != 0)", LightEnabledName);
                OpenBracket();
                wl("embDir = normalize((gl_ModelViewMatrix * vec4(embLight.{0}, 1.0)).xyz - {1});",
                    LightPosName, _vPositionName);
                CloseBracket();
                wl("{0}.st = baseUV + vec2(dot(embDir, tng), dot(embDir, bnm));", e);
            }
            CloseBracket();
            wl();
        }

        #endregion

        #region Stage code

        /// <summary>
        /// Writes the texture lookup of a TEV stage if it needs special handling:
        /// the material uses indirect texturing, or the stage's coordinate is an emboss coordinate.
        /// Returns false if the normal lookup code should be used.
        /// </summary>
        private static bool WriteIndirectTextureLookup(MDL0TEVStageNode stage, int mapID, int coordID, string tswap)
        {
            bool indirect = UsesIndirect;
            if (!indirect && !IsEmboss(coordID))
            {
                return false;
            }

            if (mapID < 0 || mapID >= _material.Children.Count || coordID < 0 || coordID >= _material.Children.Count)
            {
                return false;
            }

            string uv = StageUv(coordID);

            if (!indirect)
            {
                wl(_texColorName + " = texture2D(texture{0}, {1}.st).{2};", mapID.ToString(), uv, tswap);
                return true;
            }

            CMD cmd = stage._cmd;
            int n = (int) cmd.StageID;
            bool coordPath = cmd.Matrix != IndTexMtxID.NoMatrix || cmd.UsePrevStage;

            if (coordPath && EnsureIndirectLookup(stage, n))
            {
                WriteIndirectCoord(cmd, n, mapID, uv);
            }
            else
            {
                //Not affected by indirect texturing, but later stages can still add onto this coordinate
                wl("{0} = {1}.st * {2}[{3}];", TevCoordName, uv, _uTexSizeName, mapID.ToString());
            }

            wl(_texColorName + " = texture2D(texture{0}, {1} / {2}[{0}]).{3};",
                mapID.ToString(), TevCoordName, _uTexSizeName, tswap);
            return true;
        }

        /// <summary>
        /// Writes the raster color for the BumpAlpha / NormalizedBumpAlpha inputs.
        /// </summary>
        private static void WriteBumpAlpha(MDL0TEVStageNode stage)
        {
            CMD cmd = stage._cmd;
            if (!UsesIndirect || cmd.Alpha == IndTexAlphaSel.Off || !EnsureIndirectLookup(stage, (int) cmd.StageID))
            {
                wl(_rasColorName + " = {0};", vec4Zero);
                return;
            }

            string ch = cmd.Alpha == IndTexAlphaSel.S ? "x" : cmd.Alpha == IndTexAlphaSel.T ? "y" : "z";

            //Bump alpha is the part of the indirect texel that the offset format doesn't use
            int div = cmd.Format == IndTexFormat.F_5_Bit_Offsets ? 32 :
                cmd.Format == IndTexFormat.F_4_Bit_Offsets ? 16 : 8;
            string val = $"(floor(indRaw.{ch} / {div}.0) * {div}.0 / 255.0)";
            if (stage.RasterColor == ColorSelChan.NormalizedBumpAlpha)
            {
                val += " * (255.0 / 248.0)";
            }

            wl(_rasColorName + " = vec4({0});", val);
        }

        private static string IndWrapExpr(string coord, IndTexWrap wrap)
        {
            switch (wrap)
            {
                case IndTexWrap.Wrap256: return $"mod({coord}, 256.0)";
                case IndTexWrap.Wrap128: return $"mod({coord}, 128.0)";
                case IndTexWrap.Wrap64: return $"mod({coord}, 64.0)";
                case IndTexWrap.Wrap32: return $"mod({coord}, 32.0)";
                case IndTexWrap.Wrap16: return $"mod({coord}, 16.0)";
                case IndTexWrap.Wrap0: return "0.0";
                default: return coord;
            }
        }

        /// <summary>
        /// Makes sure the indirect texture lookup for this stage has been written. Declares
        /// indRaw (the raw 0-255 s,t,u values) and indCrd (masked for the format and biased).
        /// Returns false if the indirect stage's texture map or coordinate isn't usable.
        /// </summary>
        private static bool EnsureIndirectLookup(MDL0TEVStageNode stage, int n)
        {
            if (_indLookupStage == stage)
            {
                return _indLookupOk;
            }

            _indLookupStage = stage;
            _indLookupOk = WriteIndirectLookup(stage._cmd, n);
            return _indLookupOk;
        }

        private static bool WriteIndirectLookup(CMD cmd, int n)
        {
            if (n < 0 || n > 3 || _shaderNode == null || _material == null)
            {
                return false;
            }

            TexMapID map;
            TexCoordID coord;
            switch (n)
            {
                case 0:
                    map = _shaderNode.IndTex0MapID;
                    coord = _shaderNode.IndTex0Coord;
                    break;
                case 1:
                    map = _shaderNode.IndTex1MapID;
                    coord = _shaderNode.IndTex1Coord;
                    break;
                case 2:
                    map = _shaderNode.IndTex2MapID;
                    coord = _shaderNode.IndTex2Coord;
                    break;
                default:
                    map = _shaderNode.IndTex3MapID;
                    coord = _shaderNode.IndTex3Coord;
                    break;
            }

            int mapI = (int) map;
            int coordI = (int) coord;
            if (mapI < 0 || coordI < 0 || mapI >= _material.Children.Count || coordI >= _material.Children.Count)
            {
                return false;
            }

            Comment("Indirect Texture Stage " + n);
            wl("vec3 indRaw = vec3(0.0);");
            wl("vec3 indCrd = vec3(0.0);");
            wl("if ({0} > {1})", _uIndCountName, n.ToString());
            OpenBracket();
            {
                //The indirect texture is read as (s, t, u) = (a, b, g)
                wl("indRaw = floor(texture2D(texture{0}, {1}.st * {2}[{3}]).abg * 255.0 + 0.5);",
                    mapI.ToString(), StageUv(coordI), _uIndScaleName, n.ToString());

                string crd = "indRaw";
                switch (cmd.Format)
                {
                    case IndTexFormat.F_5_Bit_Offsets:
                        crd = "mod(indRaw, 32.0)";
                        break;
                    case IndTexFormat.F_4_Bit_Offsets:
                        crd = "mod(indRaw, 16.0)";
                        break;
                    case IndTexFormat.F_3_Bit_Offsets:
                        crd = "mod(indRaw, 8.0)";
                        break;
                }

                string bias = cmd.Format == IndTexFormat.F_8_Bit_Offsets ? "-128.0" : "1.0";
                int b = (int) cmd.Bias;
                string bx = (b & 1) != 0 ? bias : "0.0";
                string by = (b & 2) != 0 ? bias : "0.0";
                string bz = (b & 4) != 0 ? bias : "0.0";

                wl("indCrd = {0} + vec3({1}, {2}, {3});", crd, bx, by, bz);
            }
            CloseBracket();
            return true;
        }

        /// <summary>
        /// Writes the calculation of tevCoord (in texels) for a stage using indirect texturing.
        /// </summary>
        private static void WriteIndirectCoord(CMD cmd, int n, int mapID, string uv)
        {
            wl("vec2 tcBase = {0}.st * {1}[{2}];", uv, _uTexSizeName, mapID.ToString());
            wl("if ({0} > {1})", _uIndCountName, n.ToString());
            OpenBracket();
            {
                if (cmd.SWrap != IndTexWrap.NoWrap || cmd.TWrap != IndTexWrap.NoWrap)
                {
                    wl("tcBase = vec2({0}, {1});", IndWrapExpr("tcBase.x", cmd.SWrap),
                        IndWrapExpr("tcBase.y", cmd.TWrap));
                }

                string off = "vec2(0.0)";
                int m = (int) cmd.Matrix;
                if (m >= (int) IndTexMtxID.Matrix0 && m <= (int) IndTexMtxID.Matrix2)
                {
                    int i = m - (int) IndTexMtxID.Matrix0;
                    off =
                        $"vec2(dot({_uIndMtxAName}[{i}].xyz, indCrd), dot({_uIndMtxBName}[{i}].xyz, indCrd)) * {_uIndMtxAName}[{i}].w";
                }
                else if (m >= (int) IndTexMtxID.MatrixS0 && m <= (int) IndTexMtxID.MatrixS2)
                {
                    //Dynamic matrix: the stage's own texture coordinate scaled by the indirect S value.
                    //This is what turns an emboss coordinate into bump mapping.
                    int i = m - (int) IndTexMtxID.MatrixS0;
                    off = $"{_uIndMtxAName}[{i}].w * {uv}.st * indCrd.xx";
                }
                else if (m >= (int) IndTexMtxID.MatrixT0 && m <= (int) IndTexMtxID.MatrixT2)
                {
                    int i = m - (int) IndTexMtxID.MatrixT0;
                    off = $"{_uIndMtxAName}[{i}].w * {uv}.st * indCrd.yy";
                }

                wl("{0} = {1}tcBase + {2};", TevCoordName, cmd.UsePrevStage ? TevCoordName + " + " : "", off);
            }
            CloseBracket();
            wl("else");
            OpenBracket();
            wl("{0} = tcBase;", TevCoordName);
            CloseBracket();
        }

        #endregion

        #region Uniforms

        private static int SignExtend11(uint v)
        {
            v &= 0x7FF;
            return (v & 0x400) != 0 ? (int) v - 0x800 : (int) v;
        }

        private static void ReadIndMtxCmds(byte* data, uint[] words)
        {
            //Each command is 5 bytes: 0x61, register, 24 bit big endian value
            for (int k = 0; k < 3; k++)
            {
                byte* c = data + k * 5;
                if (c[0] != 0x61)
                {
                    continue;
                }

                int reg = c[1] - (int) BPMemory.BPMEM_IND_MTXA0;
                if (reg >= 0 && reg < 9)
                {
                    words[reg] = (uint) ((c[2] << 16) | (c[3] << 8) | c[4]);
                }
            }
        }

        private static void GetTextureSize(MDL0MaterialRefNode mr, out float w, out float h)
        {
            w = h = 1.0f;
            MDL0TextureNode t = mr?._texture;
            if (t == null)
            {
                return;
            }

            int iw = 0, ih = 0;
            if (t.Texture != null)
            {
                iw = t.Texture.Width;
                ih = t.Texture.Height;
            }

            if ((iw <= 0 || ih <= 0) && t.Source is TEX0Node tex)
            {
                iw = tex.Width;
                ih = tex.Height;
            }

            if (iw > 0 && ih > 0)
            {
                w = iw;
                h = ih;
            }
        }

        private static void SetIndirectUniforms(MDL0MaterialNode mat)
        {
            if (mat._activeIndStages == 0)
            {
                return;
            }

            int p = mat._programHandle;

            Uniform(p, _uIndCountName, (int) mat._activeIndStages);

            //Indirect matrices. The block has the same layout for MDL0 versions 8 through 11.
            MatIndMtxBlock blk = mat._indMtx;
            uint[] words = new uint[9];
            ReadIndMtxCmds(blk.Mtx0, words);
            ReadIndMtxCmds(blk.Mtx1, words);
            ReadIndMtxCmds(blk.Mtx2, words);

            float[] a = new float[12];
            float[] b = new float[12];
            for (int m = 0; m < 3; m++)
            {
                uint wA = words[m * 3], wB = words[m * 3 + 1], wC = words[m * 3 + 2];

                //Row 0 = (ma, mc, me), Row 1 = (mb, md, mf)
                float ma = SignExtend11(wA) / 1024.0f;
                float mb = SignExtend11(wA >> 11) / 1024.0f;
                float mc = SignExtend11(wB) / 1024.0f;
                float md = SignExtend11(wB >> 11) / 1024.0f;
                float me = SignExtend11(wC) / 1024.0f;
                float mf = SignExtend11(wC >> 11) / 1024.0f;

                int sBits = (int) (((wA >> 22) & 3) | (((wB >> 22) & 3) << 2) | (((wC >> 22) & 3) << 4));
                float scale = (float) Math.Pow(2.0, sBits - 17);

                a[m * 4 + 0] = ma;
                a[m * 4 + 1] = mc;
                a[m * 4 + 2] = me;
                a[m * 4 + 3] = scale;

                b[m * 4 + 0] = mb;
                b[m * 4 + 1] = md;
                b[m * 4 + 2] = mf;
                b[m * 4 + 3] = 0.0f;
            }

            //SRT0 animated indirect matrices. As in noclip, the matrix is built directly from the
            //animated scale/rotation/translation with the basic formula and a matrix scale of 1.
            //Slots without an SRT0 animation keep the matrix stored in the material.
            for (int m = 0; m < 3; m++)
            {
                if (mat._indirectAnimated == null || !mat._indirectAnimated[m])
                {
                    continue;
                }

                BrawlLib.Modeling.TextureFrameState fs = mat._indirectFrameStates[m];
                double theta = fs.Rotate * Math.PI / 180.0;
                float sinR = (float) Math.Sin(theta);
                float cosR = (float) Math.Cos(theta);
                var animScale = fs.Scale;
                var animTrans = fs.Translate;

                a[m * 4 + 0] = animScale._x * cosR;
                a[m * 4 + 1] = animScale._y * -sinR;
                a[m * 4 + 2] = animTrans._x;
                a[m * 4 + 3] = 1.0f;

                b[m * 4 + 0] = animScale._x * sinR;
                b[m * 4 + 1] = animScale._y * cosR;
                b[m * 4 + 2] = animTrans._y;
                b[m * 4 + 3] = 0.0f;
            }

            int loc = GL.GetUniformLocation(p, _uIndMtxAName);
            if (loc > -1)
            {
                GL.Uniform4(loc, 3, a);
            }

            loc = GL.GetUniformLocation(p, _uIndMtxBName);
            if (loc > -1)
            {
                GL.Uniform4(loc, 3, b);
            }

            //Indirect texture coordinate "divide by" values: (S, T) for indirect stages 0 through 3
            IndTexScale[] ss =
            {
                blk.SS0val.S_Scale0, blk.SS0val.T_Scale0,
                blk.SS0val.S_Scale1, blk.SS0val.T_Scale1,
                blk.SS1val.S_Scale0, blk.SS1val.T_Scale0,
                blk.SS1val.S_Scale1, blk.SS1val.T_Scale1
            };
            float[] sc = new float[8];
            for (int i = 0; i < 8; i++)
            {
                int n = (int) ss[i];
                sc[i] = 1.0f / (1 << (n < 0 ? 0 : n > 8 ? 8 : n));
            }

            loc = GL.GetUniformLocation(p, _uIndScaleName);
            if (loc > -1)
            {
                GL.Uniform2(loc, 4, sc);
            }

            //Texture sizes
            float[] ts = new float[16];
            for (int i = 0; i < 8; i++)
            {
                ts[i * 2] = ts[i * 2 + 1] = 1.0f;
                if (i < mat.Children.Count)
                {
                    GetTextureSize(mat.Children[i] as MDL0MaterialRefNode, out ts[i * 2], out ts[i * 2 + 1]);
                }
            }

            loc = GL.GetUniformLocation(p, _uTexSizeName);
            if (loc > -1)
            {
                GL.Uniform2(loc, 8, ts);
            }
        }

        #endregion
    }
}
