# LiveCode Inspector 필드 미표시 버그 수정

## 유저 보고 내용
- LiveCode/TestCC.cs에 public 필드(moveSpeed, mouseSensitivity, gravity)를 추가했는데, 에디터 Inspector에서 보이지 않음

## 분석 진행 상황

### 정적 분석 결과
- `DrawComponentFields` (ImGuiInspectorPanel.cs:4002): `type.GetFields(Public | NonPublic | Instance)` 사용 -- 올바름
- public 필드 필터: `!isPublic && !hasSerialize` 조건 -- 올바름
- `DrawValue`에서 `float` 타입 처리 -- 지원됨
- `comp.GetType()`으로 타입 조회 -- LiveCode 런타임 타입 반환 예상

### 원인 추정
- 정적 분석으로는 코드 논리에 명확한 오류를 발견하지 못함
- 가능성: LiveCode 런타임 어셈블리에서 `GetFields()`가 올바르게 필드를 반환하는지 확인 필요
- 가능성: 씬에서 TestCC 컴포넌트 인스턴스의 실제 타입이 기대와 다를 수 있음

### 진단 로그 위치
- `ImGuiInspectorPanel.cs:DrawComponentFields()` 메서드 진입부에 로그 삽입
  - 타입 FullName, 어셈블리 이름, 필드 목록을 `_diag.log`에 기록

## 테스트 절차
1. 에디터 실행: `dotnet run --project src/IronRose.RoseEditor`
2. TestCC 컴포넌트가 부착된 GameObject를 Hierarchy에서 선택
3. Inspector에서 TestCC 컴포넌트가 보이는지, 필드가 보이는지 확인
4. 프로젝트 루트의 `_diag.log` 파일 내용 확인

## 다음 단계
- `_diag.log` 분석 후 원인 특정 및 수정
