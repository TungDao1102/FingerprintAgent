using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FingerprintAgent.Tests")]
[assembly: InternalsVisibleTo("FingerprintAgent")] // Host exe (AssemblyName=FingerprintAgent) calls internal ZkNativeHost teardown
