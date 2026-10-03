`model-aliases-sample.md`는 별칭 파서 테스트가 **네트워크 없이** 돌게 하려고 직접 쓴 샘플이다.
공식 문서(https://code.claude.com/docs/en/model-config.md)는 저작물이라 공개 저장소에 사본을 두지 않는다(2026-10-01).
문서의 **모양**만 흉내 낸다: "### Model aliases" 제목, 그 아래 `**`이름`**` 표, 다음 "### " 제목에서 끝남.

- 기대 별칭: `fable, sonnet, opus, haiku`
  (`default`·`best`·`opusplan`은 계열이 아니라 제외, `sonnet[1m]`·`opus[1m]`은 `[` 때문에 제외, 중복 제외,
  구역 앞뒤의 굵은 이름도 제외)
- 실제 문서 모양이 바뀌면(앱이 받아 온 문서에서 3개 미만이 나오면 버튼을 그대로 둔다) 이 샘플도 새 모양에 맞춰 고친다.
