# 9-slice border가 Sprite PPU 변경을 무시하는 버그 수정

## 유저 보고 내용
- Sprite의 `pixels_per_unit`(PPU)을 변경하고 Apply해도 UI의 9-slice 렌더링에 아무 영향이 없음
- Unity에서는 PPU가 높으면 border가 작게, PPU가 낮으면 border가 크게 렌더링되어야 함

## 원인
`UIImage.RenderSliced()`와 `UIPanel.RenderSliced()`에서 `sprite.border` 값(sprite 텍스처 픽셀 단위)을 그대로 스크린 픽셀로 사용하고 있었음.

```csharp
// 기존 코드 - border를 PPU 변환 없이 직접 사용
float bL = border.x;
float bB = border.y;
```

Unity에서는 9-slice border를 스크린 픽셀로 변환할 때 다음 공식을 사용:
- `borderScreen = borderPixels * (referencePixelsPerUnit / spritePPU) * canvasScale`
- `referencePixelsPerUnit` 기본값 = 100

PPU=100이면 factor=1.0 (기존과 동일), PPU=200이면 factor=0.5 (border 절반 크기).

추가로, Canvas의 `scaleFactor`도 border 크기에 반영되지 않는 문제가 있었음. `screenRect`는 이미 canvas scale이 적용된 상태이므로 border도 같은 스케일을 적용해야 일관성이 있음.

## 수정 내용
1. `CanvasRenderer`에 `CurrentCanvasScale` static property 추가 - RenderNode 순회 중 현재 canvas scale을 노출
2. `UIImage.RenderSliced()`와 `UIPanel.RenderSliced()`에서 border를 `borderPixels * (100 / spritePPU) * canvasScale`로 변환

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CanvasRenderer.cs` -- `CurrentCanvasScale` static property 추가, RenderAll에서 값 설정
- `src/IronRose.Engine/RoseEngine/UI/UIImage.cs` -- RenderSliced에서 border에 PPU/canvasScale 반영
- `src/IronRose.Engine/RoseEngine/UI/UIPanel.cs` -- RenderSliced에서 border에 PPU/canvasScale 반영

## 검증
- 정적 분석으로 원인 특정 및 수정
- dotnet build 성공 확인
- PPU=100일 때 기존 동작과 동일 (factor=1.0), PPU 변경 시 비례적으로 border 크기가 변하는 것을 코드 레벨에서 확인
- 유저에게 시각적 검증 요청 필요
