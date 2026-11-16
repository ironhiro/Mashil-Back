# 마실 백엔드 이미지 (멀티 스테이지, 타깃 3개)
#   docker build --target api     -t mashil-api .
#   docker build --target worker  -t mashil-worker .
#   docker build --target migrate -t mashil-migrate .   # 배포 파이프라인에서 1회 실행 (OPS-03)
# 빌드 컨텍스트는 저장소 루트. 외부 저장소(YhLibrary) 의존 없음.
# api 이미지는 web-dist/ (웹 Mashil-Front 의 vite build 결과)를 wwwroot 로 담는다 → 한 컨테이너가 웹과 API 를 같은 도메인에서
# (Azure Container Apps). 비어 있으면(.keep 만) 웹 없이 API 만 뜬다 (VM 구성은 Caddy 가 웹을 따로 내보냄).
# CI 는 --build-arg GIT_SHA=<커밋> 을 넘긴다 → Sentry 릴리스(SENTRY_RELEASE)와 이미지 라벨.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 복원 레이어 캐시: 프로젝트 파일만 먼저 복사
COPY Directory.Build.props Directory.Packages.props Mashil.slnx dotnet-tools.json ./
COPY src/Mashil.Domain/Mashil.Domain.csproj src/Mashil.Domain/
COPY src/Mashil.Infrastructure/Mashil.Infrastructure.csproj src/Mashil.Infrastructure/
COPY src/Mashil.Import/Mashil.Import.csproj src/Mashil.Import/
COPY src/Mashil.Api/Mashil.Api.csproj src/Mashil.Api/
COPY src/Mashil.Worker/Mashil.Worker.csproj src/Mashil.Worker/
RUN dotnet restore src/Mashil.Api/Mashil.Api.csproj \
 && dotnet restore src/Mashil.Worker/Mashil.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Mashil.Api/Mashil.Api.csproj -c Release -o /out/api --no-restore \
 && dotnet publish src/Mashil.Worker/Mashil.Worker.csproj -c Release -o /out/worker --no-restore

# 마이그레이션 번들 (디자인 타임 팩토리 MashilDbContextFactory 사용 — API 설정 불필요)
FROM build AS bundle
RUN dotnet tool restore \
 && dotnet ef migrations bundle --project src/Mashil.Infrastructure --startup-project src/Mashil.Api \
      --configuration Release --self-contained -r linux-x64 -o /out/efbundle

# --- API ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
ARG GIT_SHA=dev
LABEL org.opencontainers.image.revision=$GIT_SHA
WORKDIR /app
COPY --from=build /out/api .
COPY web-dist/ ./wwwroot/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    SENTRY_RELEASE=mashil-api@$GIT_SHA
EXPOSE 8080
USER app
# 헬스체크는 Caddy(VM) 또는 Container Apps 프로브가 /healthz/ready 로 한다 (이미지에 curl 없음)
ENTRYPOINT ["dotnet", "Mashil.Api.dll"]

# --- Worker (단일 인스턴스) ---
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
ARG GIT_SHA=dev
LABEL org.opencontainers.image.revision=$GIT_SHA
WORKDIR /app
COPY --from=build /out/worker .
ENV DOTNET_ENVIRONMENT=Production \
    SENTRY_RELEASE=mashil-worker@$GIT_SHA
USER app
ENTRYPOINT ["dotnet", "Mashil.Worker.dll"]

# --- 마이그레이션 실행기 ---
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrate
ARG GIT_SHA=dev
LABEL org.opencontainers.image.revision=$GIT_SHA
WORKDIR /app
COPY --from=bundle /out/efbundle ./efbundle
USER app
# 연결 문자열은 MASHIL_DB 환경 변수 (MashilDbContextFactory)
ENTRYPOINT ["./efbundle"]
