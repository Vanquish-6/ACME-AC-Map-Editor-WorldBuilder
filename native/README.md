# Native DAT library

The shipping Windows library lives here as `acme_dat.dll`.

The editor loads this file from next to the exe. Local builds and `dotnet publish`
copy it automatically when this file exists. Do not put backend source in this
repository — only the compiled library.

Windows installer builds fail if this file is missing.
