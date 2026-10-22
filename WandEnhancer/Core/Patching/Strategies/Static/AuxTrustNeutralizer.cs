using System;
using System.Collections.Generic;
using System.IO;

namespace WandEnhancer.Core.Patching.Strategies.Static
{
    /// <summary>
    /// Rewrites WinVerifyTrust callers to return success with a stub matching each
    /// method's return type. A single `ldc.i4.0; ret` would be invalid IL for void,
    /// 64-bit, float, or reference returns and crash the aux at runtime.
    /// </summary>
    internal static class AuxTrustNeutralizer
    {
        private const string ImportName = "WinVerifyTrust";

        /// <returns>Methods stubbed, or -1 on failure.</returns>
        public static int Neutralize(string auxPath, Action<string, ELogType> log)
        {
            IReadOnlyList<DotNetImage.PInvokeCaller> callers;
            using (DotNetImage image = DotNetImage.Load(auxPath))
            {
                if (image == null)
                {
                    log?.Invoke($"[ENHANCER] {Path.GetFileName(auxPath)} is not a managed image; leaving it alone.", ELogType.Warn);
                    return -1;
                }

                callers = image.FindPInvokeCallers(ImportName);
            }

            if (callers.Count == 0)
            {
                log?.Invoke($"[ENHANCER] No {ImportName} caller found in {Path.GetFileName(auxPath)}; the aux may have changed.", ELogType.Warn);
                return -1;
            }

            int stubbed = 0;
            int skipped = 0;
            using (var stream = new FileStream(auxPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                foreach (DotNetImage.PInvokeCaller caller in callers)
                {
                    byte[] stub = StubFor(caller);
                    if (stub == null)
                    {
                        skipped++;
                        continue;
                    }

                    stream.Position = caller.IlOffset;
                    bool already = stream.ReadByte() == stub[0] && (stub.Length < 2 || stream.ReadByte() == stub[1]);
                    if (already)
                    {
                        continue;
                    }

                    stream.Position = caller.IlOffset;
                    stream.Write(stub, 0, stub.Length);
                    stubbed++;
                }
            }

            if (skipped > 0)
            {
                log?.Invoke($"[ENHANCER] Skipped {skipped} {ImportName} caller(s) with an unsupported return type; they still call the real check.", ELogType.Warn);
            }

            if (stubbed == 0 && skipped > 0)
            {
                return -1;
            }

            log?.Invoke($"[ENHANCER] Auxiliary trust check neutralised ({stubbed} method(s)).", ELogType.Info);
            return stubbed;
        }

        /// <summary>Minimal success stub for the caller's ELEMENT_TYPE return, or null to skip.</summary>
        private static byte[] StubFor(DotNetImage.PInvokeCaller caller)
        {
            if (!caller.ReturnTypeKnown)
            {
                return null;
            }

            switch (caller.ReturnTypeCode)
            {
                case 0x01: // void
                    return new byte[] { 0x2A }; // ret
                case 0x02: // boolean
                case 0x03: // char
                case 0x04: // int8
                case 0x05: // uint8
                case 0x06: // int16
                case 0x07: // uint16
                case 0x08: // int32
                case 0x09: // uint32
                    return new byte[] { 0x16, 0x2A }; // ldc.i4.0; ret
                case 0x0A: // int64
                case 0x0B: // uint64
                    return new byte[] { 0x16, 0x6A, 0x2A }; // ldc.i4.0; conv.i8; ret
                case 0x0C: // float32
                case 0x0D: // float64
                    return new byte[] { 0x16, 0x6C, 0x2A }; // ldc.i4.0; conv.r8; ret
                case 0x0E: // string
                case 0x12: // class
                case 0x14: // array
                case 0x1C: // object
                case 0x1D: // szarray
                    return new byte[] { 0x14, 0x2A }; // ldnull; ret
                default:
                    return null;
            }
        }
    }
}
