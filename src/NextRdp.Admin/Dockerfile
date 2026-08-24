FROM node:24.19-alpine AS build
WORKDIR /app
COPY src/NextRdp.Admin/package*.json ./
RUN npm ci
COPY src/NextRdp.Admin/ ./
COPY src/NextRdp.Desktop/Assets/nextrdp-logo.png /NextRdp.Desktop/Assets/nextrdp-logo.png
RUN npm run build

FROM nginx:1.31.4-alpine
COPY src/NextRdp.Admin/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
COPY LICENSE NOTICE TRADEMARKS.md THIRD-PARTY-NOTICES.md /licenses/
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --retries=5 CMD wget -q --spider http://127.0.0.1:8080/health/live || exit 1
