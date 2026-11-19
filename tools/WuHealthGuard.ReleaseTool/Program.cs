// Release helper used by .github/workflows/release.yml.
//
//   keygen                         Print a new signing key pair (private key goes in the UPDATE_SIGNING_KEY secret).
//   pubkey                         Print the public key for the private key in UPDATE_SIGNING_KEY.
//   manifest --file <exe> --version <v> --url <https url> --out <update.json> [--notes <text>] [--desktop-runtime <major>]
//                                  Hash the exe and write a signed update manifest.
//
// The private key is only ever read from the UPDATE_SIGNING_KEY environment variable, never from arguments.

using System.Text.Json;
using WuHealthGuard.Core.Updates;

const string KeyVariable = "UPDATE_SIGNING_KEY";
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

string Key() => Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } k
    ? k
    : throw new InvalidOperationException($"Set the {KeyVariable} environment variable.");

string Option(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : throw new ArgumentException($"Missing {name}.");
}

try
{
    switch (args.FirstOrDefault())
    {
        case "keygen":
            var (privateKey, publicKey) = UpdateSignature.GenerateKeyPair();
            Console.WriteLine($"Private key (GitHub secret {KeyVariable}; keep it secret):");
            Console.WriteLine(privateKey);
            Console.WriteLine();
            Console.WriteLine("Public key:");
            Console.WriteLine(publicKey);
            return 0;

        case "pubkey":
            Console.WriteLine(UpdateSignature.PublicKeyFromPrivate(Key()));
            return 0;

        case "manifest":
            var file = Option(args, "--file");
            var version = Option(args, "--version");
            var url = Option(args, "--url");
            var output = Option(args, "--out");
            var notes = args.Contains("--notes") ? Option(args, "--notes") : null;
            var runtime = args.Contains("--desktop-runtime") ? Option(args, "--desktop-runtime") : null;

            string sha256;
            using (var stream = File.OpenRead(file))
            {
                sha256 = UpdateRules.Sha256Hex(stream);
            }

            var manifest = new UpdateManifest(version, url, sha256, new FileInfo(file).Length, notes,
                UpdateSignature.Sign(version, url, sha256, Key()))
            {
                DesktopRuntime = runtime,
            };

            var publicKeyForCheck = UpdateSignature.PublicKeyFromPrivate(Key());
            if (UpdateRules.Evaluate(manifest, "0.0.0", publicKeyForCheck).Status != UpdateStatus.Available)
            {
                throw new InvalidOperationException("The manifest didn't verify. Check the version (must be > 0.0.0) and the HTTPS URL.");
            }

            File.WriteAllText(output, JsonSerializer.Serialize(manifest, json));
            Console.WriteLine($"Wrote {output} for {version} ({sha256}).");
            return 0;

        default:
            Console.Error.WriteLine("Usage: keygen | pubkey | manifest --file <exe> --version <v> --url <url> --out <file> [--notes <text>] [--desktop-runtime <major>]");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
