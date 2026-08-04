# Iyu Identity — 소비앱 통합 가이드

AddIyuIdentity/MapIyuIdentity는 아이덴티티 런타임(쿠키 + JWT Bearer, 토큰 발급, 서비스 클라이언트)만 제공한다.
소비앱은 아래를 책임진다:

1. **concrete 아이덴티티 모델**: mdd로 `@implements Iyu.Core.Identity.IUser`(또는 `@inherits Iyu.Core.Identity.IyuUser`) 등 계약 준수 엔티티 생성. (P2/P3)
2. **store 등록**: `IIdentityStore`·`IServiceClientStore`의 EF 구현을 DI에 등록(앱 DbContext 위).
   `IIdentityStore.ListServiceClientsByOwnerAsync` 의 계약은 아래 「저장소가 지켜야 할 것」 참조.
3. **권한 카탈로그**: 도메인 권한 코드(`orders.read` 등)를 `AddIyuIdentity(..., permissionCatalog)`로 전달.
4. **서명키 주입**: `IdentityTokenOptions.SigningKey`를 구성값(.env/App Setting, >=32 bytes)으로 주입. 프레임워크는 키를 소유하지 않는다.
5. **login/logout/me**: 사용자 로그인은 앱이 `IIdentityStore.FindUserByUsernameAsync`로 비밀번호 검증 후 쿠키 sign-in(기존 AuthApi 패턴 승격, P3에서 이관).
   **필수**: 쿠키 sign-in 시 부여된 권한 코드마다 클레임 타입 `permissionClaimType`(기본 `"perm"`, `AddIyuIdentity`에 전달한 값과 동일해야 함)로 클레임을 1개씩 추가해야 한다.
   이 클레임이 없으면 서비스 클라이언트(JWT)는 정상 동작하는데 사람(쿠키) 사용자만 모든 permission 정책에서 403을 받는다.
   헬퍼로 `IyuIdentityClaims.Permission(code)`를 사용할 수 있다(예: `identity.AddClaim(IyuIdentityClaims.Permission("orders.read"))`).

토큰 흐름: `POST /api/auth/token {clientId, clientSecret, grant_type:"client_credentials"}` → 단기 JWT → `Authorization: Bearer`.

## 서비스 클라이언트 — 네 가지 조작

| 메서드 | 경로 | 하는 일 |
|---|---|---|
| `POST` | `/api/service-clients` | 발급. `secret` 을 **1회만** 돌려준다 |
| `GET` | `/api/service-clients` | **소유자 자신의 것을 열거.** 폐기된 것도 포함하며 `isActive` 로 구분된다 |
| `POST` | `/api/service-clients/{id}/rotate` | 새 `secret` 발급 |
| `DELETE` | `/api/service-clients/{id}` | 폐기 |

`secret` 은 잃으면 되찾을 수 없다 — 회전한다. **`id` 는 되찾을 수 있다**: 회전·폐기가 요구하는
`id` 를 발급 응답을 잃은 뒤에 얻는 곳이 `GET` 이다. 그것이 이 엔드포인트가 있는 이유이며,
없으면 앞의 셋은 발급 응답을 보관했을 때만 쓸 수 있다.

네 경로 모두 쿠키 인증(소유자 본인)을 요구하고, 남의 클라이언트는 **404** 다(403 이 아니다 —
존재 자체를 알리지 않는다).

### 저장소가 지켜야 할 것 — `ListServiceClientsByOwnerAsync`

이 메서드는 **기본 구현이 없다.** 빈 목록을 돌려주는 기본 구현을 두면 갱신하지 않은 저장소가
컴파일된 채 모든 소유자에게 *"발급한 것이 없다"* 고 **거짓말**하게 되고, 그것은 이 엔드포인트가
고치려는 실패를 더 조용한 형태로 재생산한다. 구현 시 세 가지를 지킨다:

1. **폐기된 것도 포함**하고 `IsActive = false` 로 표시한다. 목록에서 지우면 *"그 자격증명이 아직
   살아 있나?"* 에 침묵으로 답하게 되는데, 그건 *"그런 것 없다"* 와 구별되지 않는다.
2. **소유자로 엄격히 스코프**한다. 남의 것은 «빼 주는» 것이 아니라 **보이지 않아야** 한다.
3. **권한을 같은 쿼리에서 해소**한다. 행마다 `GetServiceClientPermissionsAsync` 를 부르면
   목록 1회가 N+1 왕복이 된다.

반환 타입 `ServiceClientSummary` 에는 **비밀 재료가 되는 멤버가 아예 없다** — `IServiceClient`
는 `SecretHash` 를 갖고 있으므로, 저장소가 준 것을 그대로 돌려주면 해시가 직렬화된다.
전용 레코드를 쓰는 것이 그 보장을 «각자 기억하는 규율»이 아니라 **타입의 성질**로 만든다.

`CreatedAt` 은 **nullable 이 아니고 저장소가 댄다.** `IServiceClient` 인터페이스에는 생성 시각이
없지만, 어디서 오는지는 저장소의 몫이고 모든 저장소에 답이 있다(엔티티 베이스에서 오든, 컬럼에서
오든). 아직 보지 않은 저장소 하나 때문에 nullable 로 두면, **항상 값이 있는** 모든 소비자가
null 검사를 하게 된다.
