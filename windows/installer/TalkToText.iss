; Talk-to-Text for Windows — per-user installer.
;
; Build with build-installer.ps1, which publishes the app first. Compiling this file directly
; packages whatever is in ..\artifacts\publish.
;
; Why per-user: no UAC prompt, and %LOCALAPPDATA% is where the app looks for its model
; (see docs/PARAKEET-WINDOWS.md — Program Files would stop the app updating it).
;
; The app downloads the same files itself if this download fails (SpeechModel.cs), so
; "continue without it" never leaves the user at a terminal.
;
; Why the model is downloaded rather than bundled: it is ~670 MB against ~117 MB for the
; app, and an installer that size is painful to pass around. It is fetched once, checked
; against SHA-256, and skipped when a complete copy is already there.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppExe "TalkToText.exe"
#define ModelRepo "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/"

[Setup]
AppId={{8D3C6A2E-4F1B-4B7A-9E35-6C1A2B7D9F10}
AppName=Talk-to-Text
AppVersion={#AppVersion}
AppPublisher=Pivot Studio
DefaultDirName={localappdata}\Programs\Talk-to-Text
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=TalkToText-Setup-{#AppVersion}
SetupIconFile=..\src\TalkToText.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=Talk-to-Text
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Talk-to-Text keeps running in the tray after its window closes, so an upgrade must stop it.
CloseApplications=force

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
brazilianportuguese.ModelTitle=Baixando o modelo de voz
brazilianportuguese.ModelDescription=O Parakeet v3 (~670 MB, 25 idiomas, incluindo português) e os modelos de reunião (~27 MB) são baixados uma única vez. Depois disso tudo funciona offline.
brazilianportuguese.ModelFailed=Não foi possível baixar o modelo de voz:%n%n%1%n%nContinuar sem ele? O Talk-to-Text baixa o modelo sozinho quando for aberto com internet.
brazilianportuguese.StartupTask=Iniciar o Talk-to-Text junto com o Windows
english.ModelTitle=Downloading the speech model
english.ModelDescription=Parakeet v3 (~670 MB, 25 languages) and the meeting models (~27 MB) are downloaded once. After that, everything works offline.
english.ModelFailed=The speech model could not be downloaded:%n%n%1%n%nContinue without it? Talk-to-Text downloads it by itself the next time it opens with an internet connection.
english.StartupTask=Start Talk-to-Text when Windows starts

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Single-file and self-contained: the .NET runtime, the Windows platform layer and NAudio
; are all inside the exe.
Source: "..\artifacts\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; Except llama.cpp (meeting summaries): LLamaSharp picks the CPU variant (avx2, avx512…) by
; path at run time, so those libraries must sit loose beside the exe.
Source: "..\artifacts\publish\runtimes\win-x64\*"; DestDir: "{app}\runtimes\win-x64"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\Talk-to-Text"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Talk-to-Text"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\Talk-to-Text"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Talk-to-Text}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The tray keeps the process alive; the exe cannot be removed while it runs.
Filename: "{sys}\taskkill.exe"; Parameters: "/im {#AppExe} /f"; Flags: runhidden; RunOnceId: "StopTalkToText"

; The model and settings in %LOCALAPPDATA%\TalkToText are left in place on uninstall: the model
; is 670 MB to download again, and a reinstall should keep the user's dictionary.

[Code]
const
  ModelFileCount = 6;

var
  DownloadPage: TDownloadWizardPage;
  ModelFiles: array[0..ModelFileCount - 1] of String;
  ModelUrls: array[0..ModelFileCount - 1] of String;
  ModelDirs: array[0..ModelFileCount - 1] of String;
  ModelSizes: array[0..ModelFileCount - 1] of Integer;
  ModelHashes: array[0..ModelFileCount - 1] of String;
  ModelDownloaded: Boolean;

function ModelPath(I: Integer): String;
begin
  Result := ExpandConstant('{localappdata}\TalkToText\models\') + ModelDirs[I] + '\' + ModelFiles[I];
end;

{ Complete means every file present at its exact size. Sizes rather than hashes here: this
  runs on every install, and hashing 650 MB just to decide to skip a download is slow. }
function FileInstalled(I: Integer): Boolean;
var
  Size: Integer;
begin
  Result := FileSize(ModelPath(I), Size) and (Size = ModelSizes[I]);
end;

function ModelInstalled: Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 0 to ModelFileCount - 1 do
    if not FileInstalled(I) then
    begin
      Result := False;
      Exit;
    end;
end;

procedure AddModel(I: Integer; Dir, FileName, Url: String; Size: Integer; Hash: String);
begin
  ModelDirs[I] := Dir;
  ModelFiles[I] := FileName;
  ModelUrls[I] := Url;
  ModelSizes[I] := Size;
  ModelHashes[I] := Hash;
end;

procedure InitializeWizard;
begin
  { Speech recognition: Parakeet v3, 25 languages. }
  AddModel(0, 'parakeet-v3', 'tokens.txt', '{#ModelRepo}tokens.txt', 93939,
    'd58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d');
  AddModel(1, 'parakeet-v3', 'joiner.int8.onnx', '{#ModelRepo}joiner.int8.onnx', 6355277,
    '3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3');
  AddModel(2, 'parakeet-v3', 'decoder.int8.onnx', '{#ModelRepo}decoder.int8.onnx', 11845275,
    '179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e');
  AddModel(3, 'parakeet-v3', 'encoder.int8.onnx', '{#ModelRepo}encoder.int8.onnx', 652184281,
    'acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247');

  { Meeting transcription: Silero VAD splits speech into turns, WeSpeaker tells voices apart. }
  AddModel(4, 'meeting', 'silero_vad.onnx',
    'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx', 643854,
    '9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6');
  AddModel(5, 'meeting', 'speaker.onnx',
    'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34.onnx', 26534365,
    '5ef208a9da1453335308a6b6f4e6dfbd7e183a38b604de0a57664f45d257fe94');

  DownloadPage := CreateDownloadPage(CustomMessage('ModelTitle'), CustomMessage('ModelDescription'), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  I: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or ModelInstalled then Exit;

  DownloadPage.Clear;
  for I := 0 to ModelFileCount - 1 do
    if not FileInstalled(I) then
      { The SHA-256 makes the download page reject a truncated or altered file — a truncated
        encoder otherwise fails much later with an opaque protobuf error. }
      DownloadPage.Add(ModelUrls[I], ModelFiles[I], ModelHashes[I]);

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      ModelDownloaded := True;
    except
      if DownloadPage.AbortedByUser then
        Result := False
      else
        Result := MsgBox(FmtMessage(CustomMessage('ModelFailed'), [GetExceptionMessage]),
                         mbError, MB_YESNO) = IDYES;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
begin
  if (CurStep <> ssPostInstall) or not ModelDownloaded then Exit;

  for I := 0 to ModelFileCount - 1 do
    if FileExists(ExpandConstant('{tmp}\') + ModelFiles[I]) then
    begin
      ForceDirectories(ExtractFileDir(ModelPath(I)));
      FileCopy(ExpandConstant('{tmp}\') + ModelFiles[I], ModelPath(I), False);
    end;
end;
