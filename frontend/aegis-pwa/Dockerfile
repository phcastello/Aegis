FROM node:24-alpine AS build
WORKDIR /workspace/frontend/aegis-pwa

ARG VITE_AEGIS_API_BASE_URL
ENV VITE_AEGIS_API_BASE_URL=$VITE_AEGIS_API_BASE_URL

COPY packages/aegis-client /workspace/packages/aegis-client
RUN npm ci --prefix /workspace/packages/aegis-client
COPY frontend/aegis-pwa/package*.json ./
RUN npm ci

COPY frontend/aegis-pwa .
RUN npm run build

FROM nginx:1.27-alpine AS runtime

COPY frontend/aegis-pwa/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /workspace/frontend/aegis-pwa/dist /usr/share/nginx/html

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
