# 이슈 트래커: GitHub

이 리포의 이슈와 스펙은 GitHub 이슈로 관리한다. 모든 조작은 `gh` CLI로 한다.

## 규약

- **이슈 생성**: `gh issue create --title "..." --body "..."`. 여러 줄 본문은 heredoc을 쓴다.
- **이슈 읽기**: `gh issue view <번호> --comments`. 필요하면 `jq`로 댓글을 걸러내고 라벨도 함께 가져온다.
- **이슈 목록**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`에 `--label`, `--state` 필터를 붙인다.
- **댓글 달기**: `gh issue comment <번호> --body "..."`
- **라벨 추가·제거**: `gh issue edit <번호> --add-label "..."` / `--remove-label "..."`
- **닫기**: `gh issue close <번호> --comment "..."`

리포는 `git remote -v`로 판별한다 — 클론 안에서 실행하면 `gh`가 알아서 인식한다.

## 트리아지 대상으로서의 풀 리퀘스트

**PR을 요청 창구로 취급: 아니오.** _(외부 PR을 기능 요청으로 다루는 리포라면 `예`로 바꾼다. `/triage`가 이 플래그를 읽는다.)_

`예`로 바꾸면 PR도 이슈와 동일한 라벨·상태 체계를 따르며, `gh pr` 명령을 쓴다.

- **PR 읽기**: `gh pr view <번호> --comments`, diff는 `gh pr diff <번호>`.
- **트리아지 대상 외부 PR 목록**: `gh pr list --state open --json number,title,body,labels,author,authorAssociation,comments` 후 `authorAssociation`이 `CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR`, `NONE`인 것만 남긴다(`OWNER`/`MEMBER`/`COLLABORATOR`는 제외).
- **댓글·라벨·닫기**: `gh pr comment`, `gh pr edit --add-label`/`--remove-label`, `gh pr close`.

GitHub는 이슈와 PR이 번호 공간을 공유하므로 `#42`만 봐서는 어느 쪽인지 알 수 없다. `gh pr view 42`를 먼저 시도하고 실패하면 `gh issue view 42`로 확인한다.

## 스킬이 "이슈 트래커에 게시하라"고 할 때

GitHub 이슈를 생성한다.

## 스킬이 "해당 티켓을 가져오라"고 할 때

`gh issue view <번호> --comments`를 실행한다.

## Wayfinding 조작

`/wayfinder`가 쓴다. **맵**은 이슈 하나이고 **자식** 이슈가 티켓이 된다.

- **맵**: `wayfinder:map` 라벨이 붙은 이슈 하나. 본문에 Notes / Decisions-so-far / Fog를 담는다. `gh issue create --label wayfinder:map`.
- **자식 티켓**: 맵에 GitHub 서브이슈로 연결된 이슈(`gh api`의 sub-issues 엔드포인트). 서브이슈를 못 쓰는 환경에서는 맵 본문의 작업 목록에 추가하고 자식 본문 맨 위에 `Part of #<맵번호>`를 적는다. 라벨은 `wayfinder:<종류>`(`research`/`prototype`/`grilling`/`task`). 착수하면 담당자를 배정한다.
- **차단 관계**: GitHub **네이티브 이슈 의존성**을 쓴다. `gh api --method POST repos/<owner>/<repo>/issues/<자식>/dependencies/blocked_by -F issue_id=<차단자 DB id>`. 여기서 `<차단자 DB id>`는 `#번호`나 `node_id`가 아니라 숫자 **데이터베이스 id**다(`gh api repos/<owner>/<repo>/issues/<n> --jq .id`). GitHub는 `issue_dependencies_summary.blocked_by`로 열린 차단자 수만 보고한다. 의존성 기능을 못 쓰면 자식 본문 맨 위에 `Blocked by: #<n>, #<n>` 줄로 대신한다. 차단자가 모두 닫히면 해제된 것이다.
- **프론티어 조회**: 맵의 열린 자식들을 나열하고(`gh issue list --state open`, 맵의 서브이슈·작업 목록으로 한정), 열린 차단자가 있거나 담당자가 배정된 것을 제외한 뒤 맵 순서상 첫 번째를 고른다.
- **착수**: `gh issue edit <n> --add-assignee @me` — 세션의 첫 쓰기 작업이다.
- **해결**: `gh issue comment <n> --body "<답>"` → `gh issue close <n>` → 맵의 Decisions-so-far에 맥락 포인터(gist + 링크)를 덧붙인다.
