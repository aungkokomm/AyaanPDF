; Inno Setup script for PdfEditor - WinUI 3 PDF viewer/editor with a Rust/PDFium render core.
;
; Payload is an unpackaged, self-contained Release publish (app + WinAppSDK + .NET runtime +
; render_core.dll + pdfium.dll + the bundled sample.pdf), so the target machine needs nothing
; pre-installed.
;
; Build both steps at once with:  pwsh -File tools\build_installer.ps1
; Or compile alone with:          ISCC.exe installer\PdfEditor.iss   (after publishing first)

#define AppName    "PdfEditor"
#define AppVersion "1.12.0"
#define Publisher  "Aung Ko Ko"
#define ExeName    "PdfEditorApp.exe"
#define SrcDir     "..\PdfEditorApp\publish"
#define AppIcon    "..\PdfEditorApp\Assets\AppIcon.ico"

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
OutputBaseFilename=PdfEditor-Setup-{#AppVersion}
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
UsePreviousAppDir=yes
DisableDirPage=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; The entire self-contained publish output (exe + WinUI runtime + render_core.dll +
; pdfium.dll + sample.pdf).
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";     Filename: "{app}\{#ExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; NOTE: deliberately no [UninstallDelete] entry — the app has no persistent
; per-user state yet (annotations are in-memory only; nothing is written to
; LocalAppData). Revisit once annotations/settings gain real persistence.
