param([string]$RepositoryDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
# Compilation only: does not create a GPU device, start Studio, or execute rendering/tests.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ViewportShaderCompiler
{
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(byte[] source, UIntPtr length, string name, IntPtr defines,
        IntPtr include, string entry, string profile, uint flags, uint effectFlags, out IntPtr code, out IntPtr errors);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr BufferPointer(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate UIntPtr BufferSize(IntPtr blob);
    public static ulong Compile(byte[] source, string entry, string profile)
    {
        IntPtr code, errors;
        int result = D3DCompile(source, (UIntPtr)source.Length, "VoxelPreview.hlsl", IntPtr.Zero,
            IntPtr.Zero, entry, profile, 1u << 15, 0, out code, out errors);
        try
        {
            if(result < 0)
            {
                string message = "HLSL compilation failed: " + result;
                if(errors != IntPtr.Zero)
                {
                    IntPtr method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(errors), 3 * IntPtr.Size);
                    message = Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<BufferPointer>(method)(errors));
                }
                throw new InvalidOperationException(entry + ": " + message);
            }
            IntPtr getSize = Marshal.ReadIntPtr(Marshal.ReadIntPtr(code), 4 * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<BufferSize>(getSize)(code).ToUInt64();
        }
        finally
        {
            if(code != IntPtr.Zero) Marshal.Release(code);
            if(errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }
}
'@
$shaderSource = [IO.File]::ReadAllBytes((Join-Path $RepositoryDirectory 'src/ZhuJieJing.Renderer/Assets/VoxelPreview.hlsl'))
$hostSource = Get-Content -LiteralPath (Join-Path $RepositoryDirectory 'src/ZhuJieJing.Renderer/Controls/VoxelViewport.xaml.cs') -Raw
$entries = [regex]::Matches($hostSource, '\("([A-Za-z0-9_]+)", "([vpc]s_[45]_0)"\)')
if($entries.Count -eq 0) { throw 'No shader entry points found.' }
foreach($entry in $entries) {
    $entryName = $entry.Groups[1].Value
    $profile = $entry.Groups[2].Value
    $bytes = [ViewportShaderCompiler]::Compile($shaderSource, $entryName, $profile)
    "$entryName ($profile): $bytes bytes"
}
"Compiled $($entries.Count) shader entry points."
