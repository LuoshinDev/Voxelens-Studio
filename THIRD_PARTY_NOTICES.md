# Third-party notices

Voxelens Studio's original code is licensed under MIT by 洛神Field. The following components retain their own licenses. This inventory reflects the resolved application packages at 0.5.43; it does not relicense third-party code or Minecraft assets.

| Component | Version | License / included text |
| --- | --- | --- |
| Vortice.Windows (Wpf, D3DCompiler, Direct3D11, Direct3D9, DirectX, DXGI) | 3.8.3 | [MIT](licenses/vortice.txt) |
| Vortice.Mathematics | 2.1.0 | [MIT](licenses/vortice-mathematics.txt) |
| SharpGen.Runtime / SharpGen.Runtime.COM | 2.4.2-beta | [MIT](licenses/sharpgen.txt); package attribution: Alexandre Mutel, Jeremy Koritzinsky, Amer Koleci |
| K4os.Compression.LZ4 | 1.3.8 | [MIT](licenses/k4os-lz4.txt), Milosz Krajewski |
| K4os.Hash.xxHash | 1.0.8 | [MIT](licenses/k4os-xxhash.txt), Milosz Krajewski |
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.11 | [MIT](licenses/efcore.txt), .NET Foundation and contributors |
| SQLitePCLRaw bundle/core/provider/lib.e_sqlite3 | 2.1.12 | [Apache-2.0](licenses/sqlitepclraw.txt), Eric Sink and contributors; upstream SQLite is public domain |
| .NET runtime included in the self-contained package | 10.0.11 | [MIT](licenses/dotnet-runtime.txt) and [third-party notices](licenses/dotnet-third-party-notices.txt) |
| Windows Desktop runtime | 10.0.11 | [MIT](licenses/dotnet-windowsdesktop.txt) |
| Lucide layer geometry and UI glyphs | Adapted SVG paths | [ISC / included Feather MIT notices](licenses/lucide.txt) |

Source and retrieval information is in [licenses/README.md](licenses/README.md). Recheck this inventory when upgrading packages.

Minecraft client archives, texture packs and world contents are supplied by the user, are not covered by the Studio MIT license, and are not distributed with this project. The app icon and static sky are described in [asset provenance](docs/ASSETS.md).

## Vortice.Windows / Vortice.Wpf

The D3D11/WPF host in `ViewportDrawingSurface.cs` is adapted from the Vortice.Windows DrawingSurface implementation. The application also uses Vortice packages.

Source: https://github.com/amerkoleci/Vortice.Windows

Copyright (c) Amer Koleci and Contributors

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
