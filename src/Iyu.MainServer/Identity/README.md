# Iyu Identity — 소비앱 통합 가이드

AddIyuIdentity/MapIyuIdentity는 아이덴티티 런타임(쿠키 + JWT Bearer, 사람의 세션·액세스 토큰)만 제공한다.
그 위의 표면은 각자 켜고, **켠 표면만 자기 저장소를 요구한다**:

| 켜는 호출 | 표면 | 앱이 등록하는 저장소 |
|---|---|---|
| `AddIyuServiceClients()` | 서비스 클라이언트 — `client_credentials` grant · `/api/service-clients` 관리 끝점 · 발급된 토큰의 저장소 대조 | `IServiceClientStore` |
| `AddIyuUserCredentials()` | 로컬 비밀번호 검사 — `UserCredentialService` | `IUserCredentialStore` |
| `AddIyuRefreshTokens()` | 사람의 리프레시 토큰 — `UserTokenService` · `refresh_token` grant | `IRefreshTokenStore` |
| `AddIyuExternalSignIn()` | 외부 로그인(OIDC 등) — 아래 7번 | `IExternalLoginResolver`(저장소가 아니라 판정) |

저장소만 등록하고 그 표면을 켜지 않은 호스트는 **기동이 거부된다**(무엇을 호출할지 메시지가 말한다) — 켜지 않은 grant 는
`unsupported_grant_type`, 관리 끝점은 매핑되지 않아, 그대로 두면 기계 연동이 «폐기된 자격»처럼 조용히 실패하기 때문이다.

소비앱은 아래를 책임진다:

1. **concrete 아이덴티티 모델**: mdd로 `@implements Iyu.Core.Identity.IUser`(또는 `@inherits Iyu.Core.Identity.IyuUser`) 등 계약 준수 엔티티 생성. (P2/P3)
2. **store 등록**: 켠 표면의 저장소만(위 표) EF 구현으로 DI에 등록(앱 DbContext 위).
   `IServiceClientStore.ListServiceClientsByOwnerAsync` 의 계약은 아래 「저장소가 지켜야 할 것」 참조.
3. **권한 카탈로그**: 도메인 권한 코드(`orders.read` 등)를 `AddIyuIdentity(..., permissionCatalog)`로 전달.
   넓은 권한이 좁은 권한을 덮으면 `permissionImplications: [new("orders.write", "orders.read")]` — `orders.write` 를 가진 쪽이
   `orders.read` 정책도 충족한다(추이적). 역할마다 두 코드를 짝으로 배정하지 않는다. 발급되는 세션·토큰은 받은 코드만 싣고,
   전체 집합이 필요한 앱 코드는 `PermissionHierarchy.Expand` 로 얻는다.
4. **서명키 주입**: `IdentityTokenOptions.SigningKey`를 구성값(.env/App Setting, >=32 bytes)으로 주입. 프레임워크는 키를 소유하지 않는다.
5. **클레임 원천 등록(필수)**: `IUserClaimsSource` — 주체(계정 식별자)에 대해 «이 사람이 이 앱에서 무엇을 할 수 있는가»를 클레임으로 답한다
   (권한 코드마다 `IyuIdentityClaims.Permission(code)`, 계정이 없거나 비활성이면 `null`). **등록하지 않은 호스트는 기동이 거부된다.**
   사람을 로그인시키지 않는 앱은 `null` 만 답하는 구현을 등록한다.
6. **login/logout/me**: 끝점(라우트·응답 모양·자격 검증)은 앱의 것이다. 계정을 확인한 뒤 세션은 프레임워크가 연다 —
   쿠키는 `UserSessionService.SignInAsync(http, subject)`, 액세스 토큰은 `UserAccessTokenService.IssueAsync(subject, lifetime?)`,
   리프레시 토큰과 함께면 `UserTokenService.IssueAsync(subject)`. 셋 다 5번의 원천에 묻는다. 호출자가 지은 클레임에 서명하는 공개 경로는 없다
   (Bearer 토큰은 사는 동안 다시 읽히지 않으므로, 손으로 지은 토큰은 그 권한을 수명 내내 싣는다).
   `ClaimsPrincipal` 을 손으로 짓지 않는다 — 권한 클레임을 빠뜨리면 서비스 클라이언트(JWT)는 정상인데 사람만 전 정책에서 403 을 받던 함정이 그 자리였다.
   식별자는 프레임워크가 주체로부터 쓴다(쿠키 `ClaimTypes.NameIdentifier` · 토큰 `sub`). 읽을 때는 `IyuIdentityClaims.FindSubject(user)`.
   원천이 다른 사람을 가리키는 식별자 클레임을 답하면 로그인이 아니라 오류다.
   **쿠키 세션은 사는 동안 원천에 다시 묻는다** — `IdentityTokenOptions.SessionRevalidationInterval`(기본 5분, `TimeSpan.Zero` = 매 요청)마다.
   원천이 `null` 을 답하면 로그아웃되고, 줄어든 권한은 쿠키의 것을 대체한다(쿠키 재발급). 액세스 토큰의 리프레시가 하는 일의 쿠키판이다.
   **손으로 지은 쿠키도 예외가 아니다** — `UserSessionService` 를 거치지 않은 세션 쿠키는 처음 제시될 때 그것이 싣고 있는 식별자로 원천에서 다시
   조립된다(끝점이 스스로 더한 클레임은 사라진다). 식별자가 없는 쿠키는 로그아웃된다. `AllowExternalIdentityAsSession` 을 켠 호스트만 그런 쿠키를 그대로 둔다.

7. **외부 로그인(OIDC 등)을 붙일 때**: `AddIyuExternalSignIn()` + `MapIyuExternalSignIn()` + 앱의 `IExternalLoginResolver`.
   공급자의 답은 프레임워크의 격리 쿠키(`IyuExternalSignInDefaults.AuthenticationScheme`, 5분)에 담기고 — 원격 핸들러의 기본
   `SignInScheme` 이 된다 — `GET /api/auth/external/complete` 가 리졸버에게 «어느 로컬 계정인가»(`Provider` + `ProviderKey`)를 물어
   `UserSessionService` 로 그 계정의 세션을 연다. 공급자가 보낸 것은 세션에 들어가지 않는다. 연결·프로비저닝 정책은 리졸버(앱)의 것.
   리졸버 없이 켜면 **기동이 거부된다**. 시작은 `GET /api/auth/external/{provider}?returnUrl=…`(`returnUrl` 은 이 앱의 경로만).
   로그아웃은 `POST /api/auth/sign-out?returnUrl=…`(= `UserSessionService.SignOutAsync`) — 세션 쿠키를 끝내고, 그 세션을 연 공급자의
   핸들러가 로그아웃을 지원하면(OIDC — RP 주도 로그아웃) 공급자 세션도 끝낸다. `id_token_hint` 는 핸들러가 토큰을 보관할 때
   (`SaveTokens = true`) 세션에 함께 실린다. 외부 로그인을 켠 호스트에서도 스킴 이름 없는 `SignOutAsync()` 는 세션을 끝낸다.
   핸들러가 세션 쿠키에 직접 로그인하는 구성(격리 스킴 없음)은 여전히 **기동이 거부된다** — 공급자의 주체를 그대로 세션으로
   쓰려는 앱만 `IdentityTokenOptions.AllowExternalIdentityAsSession`(리포 README `## Identity` 의 «External OIDC / AD federation»).

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
없는 것은 빠진다). 소유자의 권한은 그 코드들이 함의하는 것까지다 — `orders.write` 만 가진 소유자도 `orders.read`
클라이언트를 낼 수 있다(함의가 선언됐을 때).

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
켜는 방법은 `AddIyuRefreshTokens()` 와 `IRefreshTokenStore`(저장) 등록이다 — 클레임은 위 5번의 `IUserClaimsSource` 가 댄다.
켜지 않으면 `POST /api/auth/token` 의 `grant_type=refresh_token` 은 `unsupported_grant_type` 이다. 사용법은 리포 README `## Identity` 의 «Refresh tokens».

### 저장소가 지켜야 할 것 — `IRefreshTokenStore`

1. **토큰 원문을 저장하지 않는다.** 저장되는 것은 `RefreshTokenRecord.TokenHash`(SHA-256) 뿐이고 조회 키도 그것이다.
2. **`TryMarkUsedAsync` 는 조건부 쓰기 한 번**이다 — `UPDATE … SET UsedAt = @at WHERE Id = @id AND UsedAt IS NULL`, 한 행이 바뀌었으면 `true`.
   읽고 나서 쓰는 두 단계로 구현하면 같은 토큰을 든 두 요청이 **둘 다** 이긴다 — 복사된 토큰이 진짜 클라이언트와 경주하는 모양이 바로 그것이다.
3. **폐기는 계열(`FamilyId`)·주체(`Subject`) 단위**로, 이미 폐기된 행은 건드리지 않는다. 시각은 인자로 받은 것을 쓴다(시계는 하나).

### 클레임 원천이 지켜야 할 것 — `IUserClaimsSource`

세션을 열 때마다 불린다 — 쿠키 로그인, 토큰 로그인, 그리고 **매 리프레시**. 비활성·삭제된 사용자에게는 `null` 을 돌려준다 — 그 리프레시는 실패하고 그 로그인 계열 전체가 폐기된다.
권한이 줄었으면 줄어든 클레임을 돌려준다 — 다음 액세스 토큰부터 반영된다(이미 나간 액세스 토큰은 수명까지 유효 — `IdentityTokenOptions.Lifetime` 을 짧게).
