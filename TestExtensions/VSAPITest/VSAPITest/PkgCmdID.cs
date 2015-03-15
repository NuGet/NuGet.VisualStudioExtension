﻿// PkgCmdID.cs
// MUST match PkgCmdID.h
using System;

namespace MicrosoftCorp.VSAPITest
{
    static class PkgCmdIDList
    {
        public const uint cmdidNuGetAPITest = 0x100;
        public const uint cmdidNuGetAPIInstallPackage = 0x200;
        public const uint cmdidNuGetAPIInstallBadSource = 0x300;

        public const uint cmdidNuGetAPIInstallPackageAsync = 0x400;
    };
}