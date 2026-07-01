# Iyu Identity — 소비앱 통합 가이드

AddIyuIdentity/MapIyuIdentity는 아이덴티티 런타임(쿠키 + JWT Bearer, 토큰 발급, 서비스 클라이언트)만 제공한다.
소비앱은 아래를 책임진다:

1. **concrete 아이덴티티 모델**: mdd로 `@implements Iyu.Core.Identity.IUser`(또는 `@inherits Iyu.Core.Identity.IyuUser`) 등 계약 준수 엔티티 생성. (P2/P3)
2. **store 등록**: `IIdentityStore`·`IServiceClientStore`의 EF 구현을 DI에 등록(앱 DbContext 위).
3. **권한 카탈로그**: 도메인 권한 코드(`orders.read` 등)를 `AddIyuIdentity(..., permissionCatalog)`로 전달.
4. **서명키 주입**: `IdentityTokenOptions.SigningKey`를 구성값(.env/App Setting, >=32 bytes)으로 주입. 프레임워크는 키를 소유하지 않는다.
5. **login/logout/me**: 사용자 로그인은 앱이 `IIdentityStore.FindUserByUsernameAsync`로 비밀번호 검증 후 쿠키 sign-in(기존 AuthApi 패턴 승격, P3에서 이관).
   **필수**: 쿠키 sign-in 시 부여된 권한 코드마다 클레임 타입 `permissionClaimType`(기본 `"perm"`, `AddIyuIdentity`에 전달한 값과 동일해야 함)로 클레임을 1개씩 추가해야 한다.
   이 클레임이 없으면 서비스 클라이언트(JWT)는 정상 동작하는데 사람(쿠키) 사용자만 모든 permission 정책에서 403을 받는다.
   헬퍼로 `IyuIdentityClaims.Permission(code)`를 사용할 수 있다(예: `identity.AddClaim(IyuIdentityClaims.Permission("orders.read"))`).

토큰 흐름: `POST /api/auth/token {clientId, clientSecret, grant_type:"client_credentials"}` → 단기 JWT → `Authorization: Bearer`.
