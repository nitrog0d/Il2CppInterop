using System.Reflection;
using Iced.Intel;
using Il2CppInterop.Common.Attributes;

namespace Il2CppInterop.Common.XrefScans;

public static class XrefScanner
{
    internal static IXrefScannerImpl ScannerImpl { get; set; }

    public static unsafe IEnumerable<XrefInstance> XrefScan(MethodBase methodBase)
    {
        if (ScannerImpl == null)
            throw new InvalidOperationException(
                "XrefScanner.ScannerImpl is not set. This is likely a bug in the code, please report it.");

        var fieldValue = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(methodBase)
            ?.GetValue(null);
        if (fieldValue == null) return Enumerable.Empty<XrefInstance>();

        CachedScanResultsAttribute? cachedAttribute = null;
        try
        {
            cachedAttribute = methodBase.GetCustomAttribute<CachedScanResultsAttribute>(false);
        }
        catch (Exception e)
        {
            Logger.Warning(
                $"Failed to get custom attribute for {methodBase.DeclaringType!.FullName}.{methodBase.Name}: {e.Message}. Falling back to scanning.");
        }

        if (cachedAttribute == null)
        {
            XrefScanUtil.CallMetadataInitForMethod(methodBase);

            return XrefScanImpl(DecoderForAddress(*(IntPtr*)(IntPtr)fieldValue));
        }

        if (cachedAttribute.XrefRangeStart == cachedAttribute.XrefRangeEnd)
            return Enumerable.Empty<XrefInstance>();

        XrefScanMethodDb.CallMetadataInitForMethod(cachedAttribute);

        return XrefScanMethodDb.CachedXrefScan(cachedAttribute).Where(it =>
            it.Type == XrefType.Method || ScannerImpl.XrefGlobalClassFilter(it.Pointer));
    }

    public static IEnumerable<XrefInstance> UsedBy(MethodBase methodBase)
    {
        var cachedAttribute = methodBase.GetCustomAttribute<CachedScanResultsAttribute>(false);
        if (cachedAttribute == null || cachedAttribute.RefRangeStart == cachedAttribute.RefRangeEnd)
            return Enumerable.Empty<XrefInstance>();

        return XrefScanMethodDb.ListUsers(cachedAttribute);
    }

    internal static unsafe Decoder DecoderForAddress(IntPtr codeStart, int lengthLimit = 1000)
    {
        if (codeStart == IntPtr.Zero) throw new NullReferenceException(nameof(codeStart));

        var stream = new UnmanagedMemoryStream((byte*)codeStart, lengthLimit, lengthLimit, FileAccess.Read);
        var codeReader = new StreamCodeReader(stream);
        var decoder = Decoder.Create(IntPtr.Size * 8, codeReader);
        decoder.IP = (ulong)codeStart;

        return decoder;
    }

    internal static IEnumerable<XrefInstance> XrefScanImpl(Decoder decoder, bool skipClassCheck = false)
    {
        while (true)
        {
            decoder.Decode(out var instruction);
            if (decoder.LastError == DecoderError.NoMoreBytes) yield break;

            if (instruction.FlowControl == FlowControl.Return)
                yield break;

            if (instruction.Mnemonic == Mnemonic.Int || instruction.Mnemonic == Mnemonic.Int1)
                yield break;

            if (instruction.Mnemonic == Mnemonic.Call || instruction.Mnemonic == Mnemonic.Jmp)
            {
                var targetAddress = ExtractTargetAddress(instruction);
                if (targetAddress != 0)
                    yield return new XrefInstance(XrefType.Method, (IntPtr)targetAddress, (IntPtr)instruction.IP);
                continue;
            }

            if (instruction.FlowControl == FlowControl.UnconditionalBranch)
                continue;

            if (IsMoveMnemonic(instruction.Mnemonic))
            {
                XrefInstance? result = null;
                try
                {
                    if (instruction.Op1Kind == OpKind.Memory && instruction.IsIPRelativeMemoryOperand)
                    {
                        var movTarget = (IntPtr)instruction.IPRelativeMemoryAddress;
                        if (instruction.MemorySize != MemorySize.UInt64)
                            continue;

                        if (skipClassCheck || ScannerImpl.XrefGlobalClassFilter(movTarget))
                            result = new XrefInstance(XrefType.Global, movTarget, (IntPtr)instruction.IP);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex.ToString());
                }

                if (result != null)
                    yield return result.Value;
            }
        }
    }

    internal static bool IsMoveMnemonic(Mnemonic mnemonic)
    {
        return mnemonic is Mnemonic.Mov or >= Mnemonic.Cmova and <= Mnemonic.Cmovs;
    }

    internal static ulong ExtractTargetAddress(in Instruction instruction)
    {
        switch (instruction.Op0Kind)
        {
            case OpKind.NearBranch16:
                return instruction.NearBranch16;
            case OpKind.NearBranch32:
                return instruction.NearBranch32;
            case OpKind.NearBranch64:
                return instruction.NearBranch64;
            case OpKind.FarBranch16:
                return instruction.FarBranch16;
            case OpKind.FarBranch32:
                return instruction.FarBranch32;
            default:
                return 0;
        }
    }
}