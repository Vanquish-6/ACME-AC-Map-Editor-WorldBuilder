# ACME native DAT access

This project has no DAT parser of its own. It loads a native library supplied at
build time and copies that library next to the app.

Set the MSBuild property `NativeDatLibraryPath` to the absolute path of the
native DLL when you need to point at a local build. Do not check private backend
sources into this repository.

This is the byte bridge used by the editor. Do not add DAT packing or unpacking
to this C# project.
