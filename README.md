# About this Repo
KortIUrDetektor is a background application that detects if a Smart Card is inserted or removed from any internal or attached smart card reader.
When a card inserted event is detected then KortIUrDetektor try to execute a command with arguments (see appsettings.json file), default no command.
When a card removed event is detected then KortIUrDetektor try to execute a command with arguments (see appsettings.json file), default the following command is executed "C:\\Program Files (x86)\\Citrix\\ICA Client\\SelfServicePlugin\\SelfService.exe" with the following arguments "-logoff -terminate", which in summary closes all ongoing citrix apps.
KortIUrDetektor never reads the content of the card and it does not care which type of smart card it is.

The windows installation file "KortIUrDetektor_x_y_z.msi", which is signed by a Region Skåne certificate, will install KortIUrDetektor.exe (also signed by a Region Skåne certificate) and appsettings.json in the following location: C:\Program Files\RegionSkane\KortIUrDetektor
The installation will also make sure, by updating the windows registry, that the KortIUrDetektor.exe is started every time a user is logging into windows.
It is possible to uninstall the app from programs menu, or upgrade the installation by simply running a newer installation file.

The configuration in the appsettings.json file can be edited.
Windows event log level can be changed between Warning, Information and Debug, but Warning is the default level so only warnings are logged. 
The commands and arguments for card inserted event and card removed event can also be changed according to your needs.

Sorry about the mix of English and Swedish texts in the documentation.

# Development
## Introduction 
Medvetet val av att använda .NET 6, men kan komma att uppdateras senare till .NET 8 om tillfälle ges.
Detta är en bakgrundsapplikation utan console som skall köra/starta så fort någon användare loggar in. Därför är den satt att vara en windows application.

## Getting Started
1. Installera Visual Studio
1. Installera wix tools för att kunna bygga msi paket:
	'dotnet tool install --global wix --version 4.0.6'
1. Installera Visual Studio Extension som heter "HeatWave for VS2022 extension", ni hittar info om det här https://marketplace.visualstudio.com/items?itemName=FireGiant.FireGiantHeatWaveDev17 annars om Visual Studio inte hittar det när ni söker på det.


## Build and Test
Just nu har vi inga unit tester.
Byggprocessen är manuell just nu.

1. Rebuild KortIUrDetektor projektet. Se till att det är Release, x64 som byggs.
2. Publish KortIUrDetektor till file/folder.
3. Rebuild KortIUrInstaller som kommer skapa en msi-fil.

# Contribution

We appreciate feedback and contributions! Before you get started, please see the following:

- [Contribution guidelines](CONTRIBUTING.md)
- [Code of Conduct guidelines](CODE_OF_CONDUCT.md)

## License

Licensing terms: Copyright Region Skåne, 2024, Licensed under the EUPL-1.2-or-later .For more details, see [The EUPL License](LICENSE.txt).

---

This binary software package also includes usage of open source libraries from PCSC (https://github.com/danm-de/pcsc-sharp) that are subject to the following BSD 2-Clause License (which can also be found at https://github.com/danm-de/pcsc-sharp?tab=License-1-ov-file):

Copyright (c) 2007-2024 Daniel Mueller <daniel@danm.de>
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, 
   this list of conditions and the following disclaimer in the documentation 
   and/or other materials provided with the distribution.

Changes to this license can be made only by the copyright author with
explicit written consent.
   
THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED 
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES 
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; 
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON 
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT 
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS 
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
