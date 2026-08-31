# MSUIClient Third-Party Notices

MSUIClient is licensed under the GNU General Public License, version 2 or (at your option) any later version. See `LICENSE` for the complete terms. The third-party works identified below are not relicensed by that statement; their original copyright notices and license terms continue to apply.

Redistributed builds should include this file, `LICENSE.txt`, and the files under `Assets/ThirdParty/`. Package versions below are the versions currently resolved by `MSUIClient.csproj`.

## Source incorporated or adapted

### StormLib

- Upstream: <https://github.com/ladislav-zezula/StormLib>
- Copyright: Copyright (c) 1999-2013 Ladislav Zezula
- License: MIT
- Use: identified portions of the managed MPQ reader, MPQ cryptography, and PKWARE `explode` implementation are ports of or adaptations from StormLib.

### Benilla

- Upstream: <https://github.com/samwhosung/benilla>
- Copyright: Copyright (c) 2026 Sam
- Upstream license: MIT or Apache-2.0, at the user's option
- License selected for MSUIClient's ports: MIT
- Use: specifically identified networking, protocol, character-creation, and UI-law code was ported or adapted from Benilla. Benilla is also used as a behavioral and rendering reference.

## Packaged libraries

### NLayer 2.0.1

- Upstream: <https://github.com/naudio/NLayer>
- Copyright: Copyright (c) 2018 Mark Heath, Andrew Ward & Contributors
- License: MIT

### Silk.NET 2.21.0

- Upstream: <https://github.com/dotnet/Silk.NET>
- Copyright: Copyright (c) 2019-2020 Ultz Limited; Copyright (c) 2021-present .NET Foundation and Contributors
- License: MIT
- Included package families: Core, GLFW, Input, Maths, OpenGL, SDL, Windowing, and the OpenGL ImGui extension packages selected by NuGet for the target runtime.

### ImGui.NET 1.89.9.3, Dear ImGui, and cimgui

- ImGui.NET: <https://github.com/ImGuiNET/ImGui.NET>
- Copyright: Copyright (c) 2017 Eric Mellino and ImGui.NET contributors
- Dear ImGui: <https://github.com/ocornut/imgui>
- Copyright: Copyright (c) 2014-2023 Omar Cornut and Dear ImGui contributors
- cimgui: <https://github.com/cimgui/cimgui>
- Copyright: Copyright (c) 2015 Stephan Dilly
- Licenses: MIT

### SkiaSharp 3.119.2

- Upstream: <https://github.com/mono/SkiaSharp>
- Copyright: Copyright (c) 2015-2016 Xamarin, Inc.; Copyright (c) 2017-2018 Microsoft Corporation
- License: MIT

Skia's native distribution incorporates additional third-party work. The complete notice supplied with SkiaSharp 3.119.2 is reproduced at `MSUIClient/Assets/ThirdParty/SkiaSharp-THIRD-PARTY-NOTICES.txt` and is copied into distributed builds.

### Microsoft .NET libraries

The resolved graph may include Microsoft.CSharp, Microsoft.DotNet.PlatformAbstractions, Microsoft.Extensions.DependencyModel, System.Buffers, System.Memory, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe, System.Text.Encodings.Web, and System.Text.Json.

- Copyright: Copyright (c) .NET Foundation and Contributors
- License: MIT

### MIT license text

The following terms apply independently to each MIT-licensed work listed above:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The applicable copyright notice(s) above and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Native window-system libraries

### GLFW 3.4.0

- Upstream: <https://github.com/glfw/glfw>
- NuGet package: Ultz.Native.GLFW 3.4.0
- Copyright: Copyright (c) 2002-2006 Marcus Geelnard; Copyright (c) 2006-2019 Camilla Löwy
- License: zlib

```text
This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would
   be appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

### SDL 2.30.1

- Upstream: <https://github.com/libsdl-org/SDL>
- NuGet package: Ultz.Native.SDL 2.30.1
- Copyright: Copyright (C) 1997-2024 Sam Lantinga <slouken@libsdl.org>
- License: zlib

```text
This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

## Development references and community acknowledgments

MSUIClient interoperates with VMaNGOS and SuperUI-Core but does not bundle either server. The project gratefully recognizes the VMaNGOS and broader MaNGOS communities, the wowemulation-dev community, warcraft-rs contributors, and the many people who documented the World of Warcraft 1.12 protocol and MPQ, BLP, DBC, ADT, M2, and WMO formats.

MangosSuperUI is a separate project in the same project family. Some format-reader and geoset work was first developed there and later carried into MSUIClient by the project author.

World of Warcraft and Blizzard Entertainment are trademarks or registered trademarks of Blizzard Entertainment, Inc. Blizzard Entertainment does not sponsor, endorse, or provide assets for MSUIClient. MSUIClient does not distribute Blizzard game data.
