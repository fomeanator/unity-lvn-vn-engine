#!/usr/bin/env python3
# МЕДЛЕННАЯ СЕТЬ, КОТОРУЮ МОЖНО ВОСПРОИЗВЕСТИ.
#
# Настоящая мобильная сеть редко «пропадает» — она тормозит, и это другой
# случай: сокет открыт, ответ идёт, но идёт по капле или замирает на середине.
# Игра обязана отличать одно от другого: медленная, но живая передача — это
# честная загрузка фона на слабой соте, а замершая — мёртвое соединение, и
# ждать его вечно значит показать игроку чёрный экран.
#
# Три режима, ровно три беды:
#
#   silent  соединение принято, ответа нет вовсе (сервер за таймаутом);
#   drip    ответ идёт кусочками с паузами — медленно, но жив;
#   stall   ответ начался и замер на середине тела.
#
#   qa/slow-server.py <silent|drip|stall> <порт> [байт] [пауза]
#
# Печатает «готов» в stdout, когда порт слушается: тот, кто его запустил, ждёт
# эту строку, а не спит наугад.
import socket, sys, threading, time

MODE = sys.argv[1]
PORT = int(sys.argv[2])
SIZE = int(sys.argv[3]) if len(sys.argv) > 3 else 4096
PAUSE = float(sys.argv[4]) if len(sys.argv) > 4 else 0.4
CHUNK = max(1, SIZE // 16)

def handle(conn):
    try:
        conn.recv(65536)
        if MODE == "silent":
            time.sleep(300)                 # не отвечаем вовсе
            return
        body = b"x" * SIZE
        conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: " + str(SIZE).encode()
                     + b"\r\nContent-Type: application/octet-stream\r\n\r\n")
        if MODE == "drip":
            for i in range(0, SIZE, CHUNK):
                conn.sendall(body[i:i + CHUNK])
                time.sleep(PAUSE)
        else:                               # stall
            conn.sendall(body[:CHUNK])
            time.sleep(300)
    except Exception:
        pass                                 # клиент оборвал — так и задумано
    finally:
        try: conn.close()
        except Exception: pass

srv = socket.socket()
srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
srv.bind(("127.0.0.1", PORT))
srv.listen(16)
print("готов", flush=True)
while True:
    c, _ = srv.accept()
    threading.Thread(target=handle, args=(c,), daemon=True).start()
