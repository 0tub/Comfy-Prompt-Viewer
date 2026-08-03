# Third-Party Notices

ComfyPromptViewer is published as a self-contained, single-file executable. That build
embeds the .NET runtime, the managed and native dependencies listed below, and the
bundled fonts. This file reproduces the copyright notices and license texts those
components require to be carried with binary distributions.

Native components of SkiaSharp and HarfBuzzSharp incorporate a further set of upstream
projects (Skia, FreeType, libpng, libjpeg-turbo, libwebp, zlib, ICU and others). Their
notices are reproduced verbatim in
[third-party/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt](third-party/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt).

## Contents

- [Bundled Fonts](#bundled-fonts) — Fraunces, Geist Mono, Noto Sans (SIL OFL 1.1)
- [Avalonia](#avalonia) (MIT)
- [SkiaSharp and HarfBuzzSharp](#skiasharp-and-harfbuzzsharp) (MIT, plus bundled native components)
- [ANGLE](#angle) (BSD-3-Clause)
- [LiteDB](#litedb) (MIT)
- [Other MIT Components](#other-mit-components)
- [.NET Runtime](#net-runtime) (MIT)

---

## Bundled Fonts

The following fonts are embedded in the application. Each is licensed under the SIL Open
Font License, Version 1.1, reproduced in full below. The copies distributed with
ComfyPromptViewer are subsetted Modified Versions of the originals; none of these
families reserve a font name.

### Fraunces

Fraunces (variable)
Copyright 2020 The Fraunces Project Authors (github.com/undercasetype/Fraunces)
License: SIL Open Font License, Version 1.1
Source: https://github.com/undercasetype/Fraunces

### Geist Mono

Geist Mono (Regular, Medium)
Copyright 2024 The Geist Project Authors (https://github.com/vercel/geist-font.git)
License: SIL Open Font License, Version 1.1
Source: https://github.com/vercel/geist-font

### Noto Sans

Noto Sans (Regular, Medium)
Copyright 2015-2021 Google LLC. All Rights Reserved.
License: SIL Open Font License, Version 1.1
Source: https://github.com/notofonts/latin-greek-cyrillic

### SIL Open Font License, Version 1.1

```
-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide development of collaborative font projects, to support the font creation efforts of academic and linguistic communities, and to provide a free and open framework in which fonts may be shared and improved in partnership with others.

The OFL allows the licensed fonts to be used, studied, modified and redistributed freely as long as they are not sold by themselves. The fonts, including any derivative works, can be bundled, embedded, redistributed and/or sold with any software provided that any reserved names are not used by derivative works. The fonts and derivatives, however, cannot be released under any other type of license. The requirement for fonts to remain under this license does not apply to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright Holder(s) under this license and clearly marked as such. This may include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the copyright statement(s).

"Original Version" refers to the collection of Font Software components as distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting, or substituting -- in part or in whole -- any of the components of the Original Version, by changing formats or by porting the Font Software to a new environment.

"Author" refers to any designer, engineer, programmer, technical writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining a copy of the Font Software, to use, study, copy, merge, embed, modify, redistribute, and sell modified and unmodified copies of the Font Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components, in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled, redistributed and/or sold with any software, provided that each copy contains the above copyright notice and this license. These can be included either as stand-alone text files, human-readable headers or in the appropriate machine-readable metadata fields within text or binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font Name(s) unless explicit written permission is granted by the corresponding Copyright Holder. This restriction only applies to the primary font name as presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font Software shall not be used to promote, endorse or advertise any Modified Version, except to acknowledge the contribution(s) of the Copyright Holder(s) and the Author(s) or with their explicit written permission.

5) The Font Software, modified or unmodified, in part or in whole, must be distributed entirely under this license, and must not be distributed under any other license. The requirement for fonts to remain under this license does not apply to any document created using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM OTHER DEALINGS IN THE FONT SOFTWARE.
```

---

## Avalonia

Avalonia 12.0.4
Avalonia.Controls.ItemsRepeater 12.0.0
Avalonia.Desktop 12.0.4
Avalonia.Themes.Fluent 12.0.4
Avalonia.Skia 12.0.4
Avalonia.HarfBuzz 12.0.4
Avalonia.Win32 12.0.4
Avalonia.X11 12.0.4
Avalonia.Native 12.0.4
Avalonia.FreeDesktop 12.0.4
Avalonia.FreeDesktop.AtSpi 12.0.4
Avalonia.Remote.Protocol 12.0.4
Avalonia.BuildServices 11.3.2

Copyright 2013-2026 © The AvaloniaUI Project
License: MIT
Source: https://github.com/AvaloniaUI/Avalonia

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors
All Rights Reserved

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
```

---

## SkiaSharp and HarfBuzzSharp

SkiaSharp 3.119.4 and SkiaSharp.NativeAssets.\* 3.119.4
HarfBuzzSharp 8.3.1.3 and HarfBuzzSharp.NativeAssets.\* 8.3.1.3
License: MIT
Source: https://github.com/mono/SkiaSharp

The native libraries shipped by these packages incorporate additional third-party
projects. Their notices are reproduced verbatim in
[third-party/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt](third-party/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt).

Those components are not all MIT-licensed. In particular, the bundled FreeType is used
under the FreeType License (FTL), which requires that the FreeType project be credited
in the documentation of products that use it:

> Portions of this software are copyright © The FreeType Project
> (www.freetype.org). All rights reserved.

```
Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## ANGLE

Avalonia.Angle.Windows.Natives 2.1.27548.20260419
License: BSD-3-Clause
Source: https://chromium.googlesource.com/angle/angle

```
Copyright 2018 The ANGLE Project Authors.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

    Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

    Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following
    disclaimer in the documentation and/or other materials provided
    with the distribution.

    Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
    Ltd., nor the names of their contributors may be used to endorse
    or promote products derived from this software without specific
    prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

---

## LiteDB

LiteDB 5.0.21
Copyright (c) 2014-2022 Mauricio David
License: MIT
Source: https://github.com/mbdavid/LiteDB

```
The MIT License (MIT)

Copyright (c) 2014-2022 Mauricio David

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
```

---

## Other MIT Components

The following components are each licensed under the MIT License, reproduced once below.

| Component | Copyright | Source |
| --- | --- | --- |
| MicroCom.Runtime 0.11.4 | Copyright 2021 © Nikita Tsukanov | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol 0.92.0 | Copyright © Tom Deseyn | https://github.com/tmds/Tmds.DBus |
| Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0 | © Microsoft Corporation. All rights reserved. | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Logging.Abstractions 8.0.0 | © Microsoft Corporation. All rights reserved. | https://github.com/dotnet/runtime |
| Microsoft.IO.RecyclableMemoryStream 3.0.1 | © Microsoft Corporation. All rights reserved. | https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |

```
The MIT License (MIT)

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
```

---

## .NET Runtime

Packaged builds are self-contained and embed the .NET 10 runtime.

Copyright (c) .NET Foundation and Contributors
License: MIT
Source: https://github.com/dotnet/runtime

The MIT License text is reproduced in the [Avalonia](#avalonia) section above.
