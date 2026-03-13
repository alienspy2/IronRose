# 디자인 가이드라인

## 메인 테마 색상

**IronRose Theme Color**: 금속의 백장미 (Metallic White Rose)

```csharp
// RGB: (230, 220, 210) - 은은한 베이지 톤
// 정규화: (0.902, 0.863, 0.824)
// Hex: #E6DCD2

// Veldrid 사용 예시
var ironRoseColor = new RgbaFloat(0.902f, 0.863f, 0.824f, 1.0f);

// Unity 스타일 Color32
var ironRoseColor32 = new Color32(230, 220, 210, 255);
```

**색상 설명**:
- 백장미의 우아하고 은은한 흰색
- 금속의 차가운 광택감
- RGB로 표현 시 따뜻한 베이지 톤
- 배경, UI 기본 색상, 엔진 로고 등에 사용

**보조 색상** (추후 정의):
- 어둡게: 회색 톤 (금속 그림자)
- 밝게: 순백색 (하이라이트)
- 강조: 장미의 붉은색 (액센트)

---
