!macro seaReaderRegister extension
  WriteRegNone SHELL_CONTEXT "Software\Classes\.${extension}\OpenWithProgids" "SeaReader.${extension}"
  WriteRegStr SHELL_CONTEXT "Software\Classes\SeaReader.${extension}" "" "SeaReader ${extension}"
  WriteRegStr SHELL_CONTEXT "Software\Classes\SeaReader.${extension}\DefaultIcon" "" "$INSTDIR\SeaReader.exe,0"
  WriteRegStr SHELL_CONTEXT "Software\Classes\SeaReader.${extension}\shell\open\command" "" '"$INSTDIR\SeaReader.exe" "%1"'
  WriteRegStr SHELL_CONTEXT "Software\Classes\Applications\SeaReader.exe\SupportedTypes" ".${extension}" ""
!macroend

!macro seaReaderUnregister extension
  DeleteRegValue SHELL_CONTEXT "Software\Classes\.${extension}\OpenWithProgids" "SeaReader.${extension}"
  DeleteRegKey /ifempty SHELL_CONTEXT "Software\Classes\.${extension}\OpenWithProgids"
  DeleteRegKey SHELL_CONTEXT "Software\Classes\SeaReader.${extension}"
!macroend

!macro customInstall
  WriteRegStr SHELL_CONTEXT "Software\Classes\Applications\SeaReader.exe" "FriendlyAppName" "SeaReader"
  WriteRegStr SHELL_CONTEXT "Software\Classes\Applications\SeaReader.exe\shell\open\command" "" '"$INSTDIR\SeaReader.exe" "%1"'
  !insertmacro seaReaderRegister "epub"
  !insertmacro seaReaderRegister "txt"
  !insertmacro seaReaderRegister "pdf"
!macroend

!macro customUnInstall
  !insertmacro seaReaderUnregister "epub"
  !insertmacro seaReaderUnregister "txt"
  !insertmacro seaReaderUnregister "pdf"
  DeleteRegKey SHELL_CONTEXT "Software\Classes\Applications\SeaReader.exe"
!macroend
