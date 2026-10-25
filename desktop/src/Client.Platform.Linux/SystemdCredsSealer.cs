using System.ComponentModel;
using System.Diagnostics;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// <c>systemd-creds</c> с ключом машины (<c>/var/lib/systemd/credential.secret</c>, только root; с TPM2 — и
/// им): расшифровать может только root этой машины, на другой машине и с копии диска без ключа — нет. Имя
/// секрета входит в шифр: секрет под одним именем не расшифровать под другим.
/// </summary>
public sealed class SystemdCredsSealer(string program, string name) : ISecretSealer
{
    private static readonly string[] Programs = ["/usr/bin/systemd-creds", "/bin/systemd-creds"];

    public string Name => "systemd-creds";

    /// <summary>systemd-creds этой машины; нет его — null (тогда секреты службе не доверить).</summary>
    public static SystemdCredsSealer? ForThisMachine(string name) =>
        Programs.FirstOrDefault(File.Exists) is { } program ? new SystemdCredsSealer(program, name) : null;

    public byte[] Seal(byte[] plain) =>
        Run(["encrypt", "--with-key=host", "--name=" + name, "-", "-"], plain) ?? throw new IOException("systemd-creds не зашифровал");

    public byte[]? Unseal(byte[] sealedData) => Run(["decrypt", "--name=" + name, "-", "-"], sealedData);

    private byte[]? Run(IReadOnlyList<string> arguments, byte[] input)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            process.StandardInput.BaseStream.Write(input);
            process.StandardInput.Close();
            copy.GetAwaiter().GetResult();
            _ = error.GetAwaiter().GetResult();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
