---
model: haiku
---

`src/IronRose.Engine/RoseEngine/BuildVersion.cs` 파일의 `BuildTime` 값을 현재 시각으로 갱신합니다.

## 절차

1. `src/IronRose.Engine/RoseEngine/BuildVersion.cs` 파일을 읽습니다.
2. `BuildTime` 값을 현재 시각(KST, `yyyy-MM-dd HH:mm:ss` 형식)으로 교체합니다.
   - 현재 시각은 `date '+%Y-%m-%d %H:%M:%S'` 명령으로 얻습니다.
3. 변경 결과를 한 줄로 보고합니다.

## 규칙

- `BuildEnv` 값은 절대 변경하지 않습니다.
- `BuildTime` 행만 수정합니다. 다른 코드는 건드리지 않습니다.
- 파일이 존재하지 않으면 "BuildVersion.cs를 찾을 수 없습니다"라고 알립니다.
