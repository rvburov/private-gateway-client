using System.Text;
using PrivateGateway.Client.Platform.MacOS;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Шифрование секретов службы macOS (<see cref="KeychainSealer"/>): с подделкой security — ключ только через stdin, имя
/// входит в шифр, испорченное и без ключа не открывается; и настоящая системная Связка ключей на macOS от root — на своем
/// имени секрета, которое тест потом удаляет.
/// </summary>
public sealed class KeychainSealerTests
{
    private static readonly byte[] Secret = "ключ сервера 11111111-2222-3333-4444-555555555555"u8.ToArray();

    [Fact]
    public void Key_goes_to_the_keychain_through_stdin_and_the_secret_comes_back_only_under_its_name()
    {
        var keychain = new FakeKeychain();
        var sealer = new KeychainSealer(keychain.Run, "pg-boot", "/tmp/test.keychain");

        var sealedData = sealer.Seal(Secret);
        var again = sealer.Seal(Secret);

        keychain.Items.Count.ShouldBe(1, "второй раз — тот же ключ");
        var key = keychain.Items.Values.Single();
        keychain.Arguments.ShouldNotContain(a => a.Contains(key, StringComparison.OrdinalIgnoreCase), "ключ — не в параметрах процесса");
        keychain.Arguments.ShouldAllBe(a => a.EndsWith("/tmp/test.keychain", StringComparison.Ordinal) || a == "-i", "только своя Связка ключей");
        Encoding.UTF8.GetString(sealedData).ShouldNotContain("11111111-2222", customMessage: "секрет — только зашифрованным");
        again.ShouldNotBe(sealedData, "у каждого шифра свой nonce");

        sealer.Unseal(sealedData).ShouldBe(Secret);
        sealer.Unseal(again).ShouldBe(Secret);
        new KeychainSealer(keychain.Run, "pg-other", "/tmp/test.keychain").Unseal(sealedData).ShouldBeNull("другое имя — другой ключ");

        keychain.Items[("PrivateGateway", "pg-other")] = key;
        new KeychainSealer(keychain.Run, "pg-other", "/tmp/test.keychain").Unseal(sealedData).ShouldBeNull("имя входит в шифр и при том же ключе");

        var broken = (byte[])sealedData.Clone();
        broken[^1] ^= 0xFF;
        sealer.Unseal(broken).ShouldBeNull("испорченное не открывается");
        sealer.Unseal([1, 2, 3]).ShouldBeNull();

        sealer.Forget();
        keychain.Items.ShouldNotContainKey(("PrivateGateway", "pg-boot"));
        sealer.Unseal(sealedData).ShouldBeNull("ключ удален — секрет не открыть");
    }

    [Fact]
    public void Keychain_that_does_not_answer_is_an_error_and_not_a_new_key()
    {
        var keychain = new FakeKeychain { Locked = true };
        keychain.Items[("PrivateGateway", "pg-boot")] = new string('a', 64);
        var sealer = new KeychainSealer(keychain.Run, "pg-boot", "/tmp/test.keychain");

        Should.Throw<IOException>(() => sealer.Seal(Secret));
        keychain.Items[("PrivateGateway", "pg-boot")].ShouldBe(new string('a', 64), "ключ, который есть, но не отдался, не затирается новым");
        sealer.Unseal(new byte[64]).ShouldBeNull();

        var readOnly = new FakeKeychain { ReadOnly = true };
        Should.Throw<IOException>(() => new KeychainSealer(readOnly.Run, "pg-boot", "/tmp/test.keychain").Seal(Secret))
            .Message.ShouldContain("не сохранила", customMessage: "ключ, которого нет в Связке ключей, — не ключ: шифр потом не открыть");
    }

    [Theory]
    [InlineData("pg boot")]
    [InlineData("pg-boot\nadd-generic-password")]
    [InlineData("")]
    public void Strange_names_do_not_reach_the_commands(string name) =>
        Should.Throw<ArgumentException>(() => new KeychainSealer(new FakeKeychain().Run, name, "/tmp/test.keychain").Seal(Secret));

    [Fact]
    public void Real_system_keychain_keeps_the_key_for_root()
    {
        if (!OperatingSystem.IsMacOS() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("системная Связка ключей — только на macOS от root (CI)");
            return;
        }

        var name = "pg-test-" + Guid.NewGuid().ToString("N")[..8];
        var sealer = KeychainSealer.ForThisMachine(name).ShouldNotBeNull();
        try
        {
            var sealedData = sealer.Seal(Secret);
            sealer.Seal(Secret).ShouldNotBe(sealedData);
            KeychainSealer.ForThisMachine(name)!.Unseal(sealedData).ShouldBe(Secret, "новый экземпляр (перезапуск службы) открывает тем же ключом");
            KeychainSealer.ForThisMachine(name + "-b")!.Unseal(sealedData).ShouldBeNull();

            sealer.Forget();
            sealer.Unseal(sealedData).ShouldBeNull("ключ удален");
        }
        finally
        {
            sealer.Forget();
        }
    }

    /// <summary>Подделка security: записи Связки ключей, параметры всех вызовов и команды security -i.</summary>
    private sealed class FakeKeychain
    {
        public Dictionary<(string Service, string Account), string> Items { get; } = [];

        public List<string> Arguments { get; } = [];

        public bool Locked { get; init; }

        public bool ReadOnly { get; init; }

        public (int Code, string Output, string Error) Run(IReadOnlyList<string> arguments, string? input)
        {
            Arguments.Add(string.Join(' ', arguments));
            if (Locked && arguments[0] != "-i")
            {
                return (51, string.Empty, "security: SecKeychainSearchCopyNext: User interaction is not allowed.");
            }

            if (arguments is ["-i"])
            {
                foreach (var line in (input ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var words = line.Split(' ');
                    words[0].ShouldBe("add-generic-password");

                    // Как security: запись с тем же именем без -U не заменяется.
                    if (!ReadOnly && !words.Contains("-U"))
                    {
                        Items.TryAdd((Value(words, "-s"), Value(words, "-a")), Value(words, "-w"));
                    }
                }

                return (0, "security> security> ", string.Empty);
            }

            var key = (Value(arguments, "-s"), Value(arguments, "-a"));
            switch (arguments[0])
            {
                case "find-generic-password":
                    return Items.TryGetValue(key, out var password)
                        ? (0, password + "\n", string.Empty)
                        : (44, string.Empty, "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.");
                case "delete-generic-password":
                    return Items.Remove(key) ? (0, string.Empty, string.Empty) : (44, string.Empty, "not found");
                default:
                    throw new InvalidOperationException("неизвестная команда: " + arguments[0]);
            }
        }

        private static string Value(IReadOnlyList<string> words, string option) => words[words.ToList().IndexOf(option) + 1];
    }
}
