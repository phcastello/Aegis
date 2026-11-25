FROM node:24-alpine AS build
WORKDIR /workspace/frontend/aegis-pwa

# This legacy Compose argument is intentionally unused: the gateway is same-origin.
ARG VITE_AEGIS_API_BASE_URL
COPY frontend/aegis-pwa/package*.json ./
RUN npm ci

COPY frontend/aegis-pwa .
RUN npm run build

FROM nginx:1.27-alpine AS runtime

COPY frontend/aegis-pwa/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /workspace/frontend/aegis-pwa/dist /usr/share/nginx/html

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
