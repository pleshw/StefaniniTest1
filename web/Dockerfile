# Build context is the repository root: docker build -f web/Dockerfile .
FROM node:22 AS build
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-fund --no-audit
COPY web/ ./
RUN npm run build

# nginx serves the built pages and proxies /bff to the BFF with the same configuration compose uses.
FROM nginx:1.31-alpine AS final
COPY deploy/nginx/nginx.conf /etc/nginx/nginx.conf
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 8080
