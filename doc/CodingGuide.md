# IronRose 코딩 가이드

## 코딩 스타일

### 크로스 플랫폼
- **파일 경로**: 항상 `Path.Combine()`을 사용. `"foo/bar"` 또는 `"foo\\bar"` 금지.
- **줄 끝**: LF 기본 (`.editorconfig`, `.gitattributes` 참조)

### 인코딩
- **C# 소스 파일(.cs)**: UTF-8 with BOM 사용
- **.editorconfig**에 명시되어 자동 적용됨

### 네이밍 컨벤션
- Unity와 유사한 C# 표준 컨벤션 사용
- 클래스/메서드: PascalCase
- 필드/변수: camelCase
- 상수: UPPER_CASE

### Inspector 편집 필드 규칙
- 모든 DragFloat/DragInt 필드는 **싱글클릭으로 텍스트 편집 진입**해야 함
- `ImGui.DragFloat` 직접 사용 금지 → `DragFloatClickable`, `DragFloat2Clickable`, `DragFloat3Clickable`, `DragIntClickable` 헬퍼 사용
- 새로운 Inspector 필드 추가 시 반드시 위 헬퍼를 사용할 것

---

## Unity와의 차이점

### 스크립트 파일 위치 제한
- Unity에서는 `Assets/` 폴더 하위에 `.cs` 스크립트 파일을 자유롭게 배치하지만, **IronRose에서는 `Assets/` 폴더에 `.cs` 파일을 추가할 수 없음**
- 모든 C# 스크립트는 반드시 **Scripts** 프로젝트에만 추가해야 함
- `Assets/` 폴더는 텍스처, 모델, 씬, 프리팹 등 **비코드 에셋 전용**

### Prefab Override 미지원
- Unity에서는 Prefab을 씬에 배치하거나 다른 Prefab 안에 Sub Prefab으로 넣을 때 개별 속성값을 override할 수 있지만, **IronRose에서는 Prefab 인스턴스의 값 override를 지원하지 않음**
- Prefab을 씬에 배치하면 원본 Prefab의 값이 그대로 사용됨
- Sub Prefab (Nested Prefab)도 마찬가지로 원본 값 그대로 사용됨
- **값을 변경하려면 반드시 Prefab Variant를 생성**하여 Variant에서 원하는 값을 수정해야 함

---
