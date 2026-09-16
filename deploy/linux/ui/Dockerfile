# 使用Nginx作为基础镜像来服务静态文件
FROM nginx:1.28.1-alpine3.23
COPY files /var/www/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]