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

토큰 흐름: `POST /api/auth/token` → 단기 JWT → `Authorization: Bearer`. 요청은 **RFC 6749 §4.4 client credentials** 모양이다:

```http
POST /api/auth/token
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(urlencode(client_id) ":" urlencode(client_secret))

grant_type=client_credentials&scope=orders.read
```

- 자격은 HTTP Basic(권장, §2.3.1) 또는 form 본문의 `client_id`·`client_secret` — **둘 중 하나만**(둘 다면 `invalid_request`).
- `scope`(선택, 공백 구분): 클라이언트 유효 권한의 부분집합이면 그만큼 좁힌 토큰, 넘으면 `invalid_scope`. 없으면 유효 권한 전부.
- 오류는 §5.2 `{ "error": "…" }` — 자격 실패는 `invalid_client`(Basic 이면 401 + `WWW-Authenticate: Basic`, 본문이면 400).
  응답은 전부 `Cache-Control: no-store`.
- 종전의 JSON 본문(`{clientId, clientSecret, grant_type}`)도 계속 받는다 — **비표준**이며, 새 연동은 위 모양을 쓴다.

## 서비스 클라이언트 — 다섯 가지 조작

| 메서드 | 경로 | 하는 일 |
|---|---|---|
| `POST` | `/api/service-clients` | 발급. `secret` 을 **1회만** 돌려준다 |
| `GET` | `/api/service-clients` | **소유자 자신의 것을 열거.** 폐기된 것도 포함하며 `isActive` 로 구분된다 |
| `POST` | `/api/service-clients/{id}/rotate` | 새 `secret` 발급 |
| `PATCH` | `/api/service-clients/{id}/permissions` | 권한 집합 교체. `secret` 은 그대로 |
| `DELETE` | `/api/service-clients/{id}` | 폐기 |

`secret` 은 잃으면 되찾을 수 없다 — 회전한다. **`id` 는 되찾을 수 있다**: 회전·폐기가 요구하는
`id` 를 발급 응답을 잃은 뒤에 얻는 곳이 `GET` 이다. 그것이 이 엔드포인트가 있는 이유이며,
없으면 앞의 셋은 발급 응답을 보관했을 때만 쓸 수 있다.

**폐기·회전·권한 교체는 이미 발급된 토큰에도 닿는다.** 서비스 클라이언트 토큰은 발급 시점의 시크릿·유효 권한
지문(`sc_stamp` 클레임)을 싣고, Bearer 검증 단계가 저장소의 현재 상태와 대조한다 — 폐기·만료·회전·권한 교체 뒤
기존 토큰은 **401** 이다. 확인 결과는 `IdentityTokenOptions.ServiceClientValidationWindow`(기본 30초) 동안 재사용되므로,
**변경이 사용 중인 토큰에 닿기까지 최대 그 시간**이 걸린다(`TimeSpan.Zero` 면 매 요청 확인). 끄려면
`ValidateServiceClientTokens = false` — 그러면 위 세 조작은 다음 발급만 막고, 기존 토큰은 `Lifetime` 끝까지 유효하다.
이 확인은 JWT Bearer 의 `OnTokenValidated` 이벤트로 등록된다 — 호스트가 `JwtBearerOptions.Events` 를 통째로 바꾸면
사라지므로, 이벤트를 덧붙이는 방식으로 설정할 것.

다섯 경로 모두 쿠키 인증(소유자 본인)을 요구하고, 남의 클라이언트는 **404** 다(403 이 아니다 —
존재 자체를 알리지 않는다).

### 권한만 바꾸고 싶을 때 — `rotate` 와의 차이

`rotate`는 시크릿을 새로 발급한다(상대방이 새 값을 재배포해야 함). **권한만 조정하고 싶다면**
`PATCH .../permissions`를 쓴다 — 시크릿은 그대로 유지되어 통신이 끊기지 않는다. `POST`(발급)와
같은 `subset ⊆ owner` 규칙이 적용된다: 요청한 권한이 소유자 자신의 권한을 넘으면 `400
{ error: "permissions_exceed_owner", exceeding: [...] }`이고, 넘지 않는 요청은 소유자 권한과의
교집합(`PermissionScope.Effective`)으로 **전체 교체**된다(병합이 아니다 — 이전 권한 중 새 목록에
없는 것은 빠진다).

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

## 사람 사용자의 리프레시 토큰 — `UserTokenService`

네이티브·데스크톱 같은 공개 클라이언트에 긴 수명 액세스 토큰 대신 «짧은 액세스 + 회전 리프레시»를 준다.
켜는 방법은 포트 두 개를 등록하는 것뿐이다 — `IRefreshTokenStore`(저장) · `IUserTokenClaimsSource`(주체의 클레임, `null` 이면 거절).
둘이 없으면 `POST /api/auth/token` 의 `grant_type=refresh_token` 은 `unsupported_grant_type` 이다. 사용법은 리포 README `## Identity` 의 «Refresh tokens».

### 저장소가 지켜야 할 것 — `IRefreshTokenStore`

1. **토큰 원문을 저장하지 않는다.** 저장되는 것은 `RefreshTokenRecord.TokenHash`(SHA-256) 뿐이고 조회 키도 그것이다.
2. **`TryMarkUsedAsync` 는 조건부 쓰기 한 번**이다 — `UPDATE … SET UsedAt = @at WHERE Id = @id AND UsedAt IS NULL`, 한 행이 바뀌었으면 `true`.
   읽고 나서 쓰는 두 단계로 구현하면 같은 토큰을 든 두 요청이 **둘 다** 이긴다 — 복사된 토큰이 진짜 클라이언트와 경주하는 모양이 바로 그것이다.
3. **폐기는 계열(`FamilyId`)·주체(`Subject`) 단위**로, 이미 폐기된 행은 건드리지 않는다. 시각은 인자로 받은 것을 쓴다(시계는 하나).

### 클레임 원천이 지켜야 할 것 — `IUserTokenClaimsSource`

로그인 때와 **매 리프레시 때** 불린다. 비활성·삭제된 사용자에게는 `null` 을 돌려준다 — 그 리프레시는 실패하고 그 로그인 계열 전체가 폐기된다.
권한이 줄었으면 줄어든 클레임을 돌려준다 — 다음 액세스 토큰부터 반영된다(이미 나간 액세스 토큰은 수명까지 유효 — `IdentityTokenOptions.Lifetime` 을 짧게).
