; Inno Setup script for PdfEditor - WinUI 3 PDF viewer/editor with a Rust/PDFium render core.
;
; Payload is an unpackaged, self-contained Release publish (app + WinAppSDK + .NET runtime +
; render_core.dll + pdfium.dll + the bundled sample.pdf), so the target machine needs nothing
; pre-installed.
;
; Build both steps at once with:  pwsh -File tools\build_installer.ps1
; Or compile alone with:          ISCC.exe installer\PdfEditor.iss   (after publishing first)

#define AppName    "Ayaan PDF"
#define Publisher  "Aung Ko Ko"
#define ExeName    "Ayaan PDF.exe"
#define SrcDir     "..\PdfEditorApp\publish"
#define AppIcon    "..\PdfEditorApp\Assets\AppIcon.ico"

; Version comes off the published exe, which gets it from <Version> in
; PdfEditorApp.csproj. Hardcoding it here is how the installer ended up saying
; 1.15.0 while the app's own About dialog said 1.0.0: two sources of truth, and
; the one the user can see was the stale one. Compiling before publishing now
; fails loudly here rather than shipping a wrong number.
#define AppVersion GetStringFileInfo(SrcDir + "\" + ExeName, "ProductVersion")

[Setup]
; Stable identity for upgrades. Never change this across versions.
AppId={{9AE88991-9B11-4726-8A61-02047BEAC716}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppComments=WinUI 3 PDF viewer and editor with a Rust/PDFium render core
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=..\dist
OutputBaseFilename=AyaanPDF-Setup-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}
; Per-user install so no admin/UAC prompt is needed. Combined with the visible
; destination page below, this also allows a separate PORTABLE instance to be
; installed to any folder (a USB stick, a test directory) alongside the main one.
;
; Deliberately NO PrivilegesRequiredOverridesAllowed. It used to be "dialog",
; which asks the user to choose all-users vs just-me — but a silent install has
; nobody to ask, so it silently chose ALL-USERS: app files landed in the
; per-user folder while the shortcuts went to the common Start Menu and the
; uninstall entry to HKLM. A half-per-user, half-machine-wide install, and it
; needed UAC. Omitting the directive pins it to per-user everywhere, which is
; consistent and never prompts. The destination page still allows a portable
; install to any writable folder.
PrivilegesRequired=lowest
; Deliberately NO, for now. Every directory recorded by an earlier install was
; written under the app's old name, so honouring it proposed
; "...\Programs\PdfEditor" no matter what DefaultDirName says, and the wrong
; name would have propagated through every future upgrade. The destination page
; is still shown, so a portable install to a chosen folder is unaffected, and an
; upgrade of a default install lands on the same default path.
;
; Worth turning back on once no machine has a PdfEditor-era directory left.
UsePreviousAppDir=no
DisableDirPage=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Tells Explorer to re-read file types, so Open with lists the app at once.
ChangesAssociations=WizardIsTaskSelected('pdffiles')

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
; On by default. A portable install to a test folder should leave it off, or
; PDFs would open in that copy instead of the main one.
Name: "pdffiles"; Description: "Offer {#AppName} for &PDF files (Open with, and Default apps in Settings)"; GroupDescription: "PDF files:"

[Registry]
; Per user (HKCU), like the rest of this install. It OFFERS the app for PDFs
; and never takes over the default: Windows lets only the user choose that,
; in Open with or in Settings > Default apps. Everything here is removed on
; uninstall.
;
; The document type the app opens PDFs as.
Root: HKA; Subkey: "Software\Classes\AyaanPDF.Document"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey; Tasks: pdffiles
Root: HKA; Subkey: "Software\Classes\AyaanPDF.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#ExeName},0"; Tasks: pdffiles
Root: HKA; Subkey: "Software\Classes\AyaanPDF.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""; Tasks: pdffiles
; Listed under Open with for .pdf.
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "AyaanPDF.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdffiles
Root: HKA; Subkey: "Software\Classes\Applications\{#ExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdffiles
Root: HKA; Subkey: "Software\Classes\Applications\{#ExeName}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: pdffiles
Root: HKA; Subkey: "Software\Classes\Applications\{#ExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#ExeName}"" ""%1"""; Tasks: pdffiles
; Listed in Settings > Default apps, where the user can make it the default.
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: pdffiles
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "View, edit, sign and recognise text in PDF files."; Tasks: pdffiles
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "AyaanPDF.Document"; Tasks: pdffiles
Root: HKA; Subkey: "Software\{#AppName}"; Flags: uninsdeletekeyifempty; Tasks: pdffiles
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue; Tasks: pdffiles

[Files]
; The entire self-contained publish output (exe + WinUI runtime + render_core.dll +
; pdfium.dll + sample.pdf).
;
; EXCLUDES, both of which matter:
;
; diag.log is WRITTEN INTO this folder whenever the app runs there with
; PDFEDITOR_DIAG=1, which is exactly how the publish output gets verified.
; Without this it ships inside the installer and every user's first launch
; appends to a trace of someone else's session.
;
; Stamps is the user's own PNG library, which the app creates beside the exe
; and which is written to from inside the app. It must never appear in the
; payload: shipping it would push test images onto users, and worse, an
; upgrade would write into a folder holding files they put there by hand.
; The installer's job is the program; that folder is data.
Source: "{#SrcDir}\*"; DestDir: "{app}"; Excludes: "diag.log,Stamps,Stamps\*"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; NO [UninstallDelete], and this is now a deliberate protection rather than
; merely nothing to clean up.
;
; The app keeps a Stamps folder beside the exe holding the user's own PNG
; images, put there by them. Inno removes only the files it installed, and
; Stamps is excluded from the payload above, so an uninstall leaves it alone.
; Adding an UninstallDelete for {app} would wipe it, which means deleting
; someone's signature and seal images because they uninstalled a PDF viewer.
;
; If a future version adds settings, they get the same treatment: the
; uninstaller removes the program, never the user's own files.

