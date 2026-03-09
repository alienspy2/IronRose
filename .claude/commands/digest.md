현재 IDE에서 열려 있는 LiveCode 파일을 FrozenCode로 승격(promote)합니다.
LiveCode → FrozenCode 방향으로만 동작합니다. (반대 방향 아님)

## 절차

1. 현재 IDE에서 열린 파일(`ide_opened_file` 또는 `ide_selection`)을 확인합니다.
2. 해당 파일이 `LiveCode/` 안의 .cs 파일인지 검증합니다.
3. `FrozenCode/`에 동일 이름 파일이 이미 존재하면 "FrozenCode에 동일 파일이 이미 존재합니다"라고 알리고 중단합니다.
4. 파일을 `LiveCode/`에서 `FrozenCode/`로 이동합니다.
5. `dotnet build`로 빌드를 검증합니다.
6. 빌드 실패 시 파일을 `LiveCode/`로 되돌리고 원래 상태로 복원합니다.
7. 결과를 보고합니다.

## 규칙

- 대상은 현재 IDE에서 선택/열린 파일입니다. 복수 파일 선택을 허용합니다.
- 대상 파일이 `LiveCode/` 경로가 아니면 "LiveCode 파일이 아닙니다"라고 알립니다.
- 대상 파일이 .cs 파일이 아니면 "digest 대상이 아닙니다"라고 알립니다.
- 빌드 실패 시 반드시 원래 상태로 복원합니다.
