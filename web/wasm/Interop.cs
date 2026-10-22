using System.Runtime.InteropServices.JavaScript;
using System.Security.Cryptography;
using System.Text;

// entry point required by the wasm sdk; all work happens through the [JSExport] methods below
public static class WasmEntry
{
    public static void Main() { }
}

public static partial class Interop
{
    private static byte[]? lastOutput;

    /// patches the given Assembly-CSharp.dll bytes and returns a JSON result.
    /// the patched bytes are kept aside and fetched with TakeOutput().
    [JSExport]
    public static string Patch(byte[] input)
    {
        lastOutput = null;
        string inputSha = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        PatchResult r;
        try
        {
            r = PatchApi.Patch(input, Silksong43.Verify.VerifyMode.ReferenceFree, null);
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"reason\":\"error\",\"log\":" + Q(ex.ToString()) +
                   ",\"passed\":0,\"failed\":0,\"skipped\":0,\"failures\":[],\"inputSha256\":" + Q(inputSha) +
                   ",\"outputSha256\":null,\"version\":" + Q(PatchApi.Version) + ",\"patchMillis\":0,\"verifyMillis\":0}";
        }
        lastOutput = r.Output;

        var log = r.Log;
        if (!string.IsNullOrEmpty(r.VerifyLog)) log += "\n--- verification ---\n" + r.VerifyLog;
        log += "\n" + r.Summary;

        var sb = new StringBuilder(log.Length + 512);
        sb.Append("{\"ok\":").Append(r.Ok ? "true" : "false");
        sb.Append(",\"reason\":").Append(Q(r.Reason));
        sb.Append(",\"log\":").Append(Q(log));
        sb.Append(",\"passed\":").Append(r.Passed);
        sb.Append(",\"failed\":").Append(r.Failed);
        sb.Append(",\"skipped\":").Append(r.Skipped);
        sb.Append(",\"failures\":[");
        for (int i = 0; i < r.Failures.Length; i++) { if (i > 0) sb.Append(','); sb.Append(Q(r.Failures[i])); }
        sb.Append("],\"inputSha256\":").Append(Q(inputSha));
        sb.Append(",\"outputSha256\":").Append(r.Sha256 == null ? "null" : Q(r.Sha256));
        sb.Append(",\"version\":").Append(Q(r.Version));
        sb.Append(",\"patchMillis\":").Append(r.PatchMillis);
        sb.Append(",\"verifyMillis\":").Append(r.VerifyMillis);
        sb.Append('}');
        return sb.ToString();
    }

    /// the patched bytes from the last Patch call (null when it produced none); cleared after the call.
    [JSExport]
    public static byte[]? TakeOutput()
    {
        var o = lastOutput;
        lastOutput = null;
        return o;
    }

    [JSExport]
    public static string GetVersion() => PatchApi.Version;

    private static string Q(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
