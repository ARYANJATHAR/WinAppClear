# Third-party software notices

WinAppClear source is MIT licensed; see the installed LICENSE file. Its self-contained Windows build includes components distributed under their own licenses. No ownership of those components is claimed.

- .NET runtime and Windows Desktop/WPF runtime, Microsoft and contributors: MIT. https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and https://github.com/dotnet/wpf/blob/main/LICENSE.TXT
- .NET third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.txt
- CommunityToolkit.Mvvm, .NET Foundation and contributors: MIT. https://github.com/CommunityToolkit/dotnet/blob/main/License.md
- System.ServiceProcess.ServiceController and Microsoft.Windows.SDK.NET.Ref: Microsoft .NET/Windows SDK components subject to their distributed license and notice files.

The installer includes extracted license/notice texts from the actual restored dependencies and runtime packs in `THIRD-PARTY-LICENSES.txt`. Preserve that file with the distribution. These notices do not grant rights beyond each dependency's own license terms.

Windows Package Manager, Android Platform Tools and browsers are separate optional external tools, not bundled installers. Their publishers' terms apply. Video-generation tools and development/test dependencies are not shipped with the app.
