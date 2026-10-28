#!/usr/bin/env bash
# Проверка wgswitch-server.sh без root и без настоящего WireGuard: поддельные wg/awg, временный /etc.
# Запуск (Linux/WSL): bash server/test/run.sh
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SCRIPT=$HERE/../wgswitch-server.sh
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
export WGS_ETC=$T/etc WGS_NO_ROOT=1 LOG=$T/calls.log
mkdir -p "$T/bin" "$WGS_ETC/wireguard" "$WGS_ETC/amnezia/amneziawg" "$WGS_ETC/wgswitch"

# поддельные утилиты: ключи настоящего вида, set/show пишут в журнал
for t in wg awg; do
    cat >"$T/bin/$t" <<'EOF'
#!/usr/bin/env bash
case $1 in
    genkey) head -c 32 /dev/urandom | base64 ;;
    pubkey) read -r k; printf '%s' "$k" | sha256sum | cut -c1-64 | xxd -r -p | base64 ;;
    show) exit 0 ;;
    set) echo "$(basename "$0") $*" >>"$LOG" ;;
esac
EOF
    chmod +x "$T/bin/$t"
done
printf '#!/bin/sh\necho testhost\n' >"$T/bin/hostname"; chmod +x "$T/bin/hostname"
export PATH=$T/bin:$PATH

fail() { echo "FAIL: $*"; exit 1; }
pass() { echo "ok   $*"; }

# сервер как у ztv до починки: ключ в файле и PostUp
SERVER_KEY=$(wg genkey); echo "$SERVER_KEY" >"$T/server.key"
cat >"$WGS_ETC/wireguard/wg0.conf" <<EOF
[Interface]
Address = 10.9.0.1/24
ListenPort = 51820
PostUp = wg set %i private-key $T/server.key

[Peer]
# ПК
PublicKey = gc2S60E5/hNgfq4fkgpXQXxcClJYobTmAj8SS13t1A0=
AllowedIPs = 10.9.0.2/32
EOF
cat >"$WGS_ETC/amnezia/amneziawg/awg0.conf" <<EOF
[Interface]
Address = 10.19.0.1/24
ListenPort = 51821
PrivateKey = $SERVER_KEY
Jc = 4
Jmin = 40
Jmax = 70
S1 = 33
S2 = 77
H1 = 111
H2 = 222
H3 = 333
H4 = 444
EOF
echo 153.80.178.94 >"$WGS_ETC/wgswitch/wg0.endpoint"
echo 153.80.178.94 >"$WGS_ETC/wgswitch/awg0.endpoint"

run() { # ответы построчно → команда
    local answers=$1; shift
    printf '%b' "$answers" >"$T/answers"
    WGS_TTY=$T/answers bash "$SCRIPT" "$@" >"$T/out" 2>&1 || { cat "$T/out"; fail "команда $* упала"; }
}

# 1. телефон в awg0: туннель 2, имя phone, IP по умолчанию, имя сервера по умолчанию, не весь интернет
run '2\nphone\n\n\nn\n' add
C=$WGS_ETC/wgswitch/clients/awg0-phone.conf
[ -f "$C" ] || fail "нет конфига телефона"
grep -q '^# WgSwitch name=testhost-awg ping=10.19.0.1$' "$C" || fail "строка WgSwitch: $(head -1 "$C")"
grep -q '^Address = 10.19.0.2/32$' "$C" || fail "адрес телефона"
for k in 'Jc = 4' 'S1 = 33' 'H4 = 444'; do grep -q "^$k\$" "$C" || fail "нет параметра $k"; done
grep -q '^Endpoint = 153.80.178.94:51821$' "$C" || fail "Endpoint"
grep -q '^AllowedIPs = 10.19.0.0/24$' "$C" || fail "маршрут"
[ "$(grep '^PublicKey' "$C" | cut -d' ' -f3)" = "$(echo "$SERVER_KEY" | wg pubkey)" ] || fail "ключ сервера в конфиге телефона"
grep -q '^# phone$' "$WGS_ETC/amnezia/amneziawg/awg0.conf" || fail "пир телефона не записан в файл"
grep -q '^awg set awg0 peer .* allowed-ips 10.19.0.2/32$' "$LOG" || fail "awg set не вызван"
[ "$(stat -c %a "$C")" = 600 ] || fail "права на конфиг телефона $(stat -c %a "$C")"
grep -q PrivateKey "$T/out" && fail "приватный ключ попал на экран"
pass "добавление телефона в AmneziaWG"

# 2. ПК по ключу в wg0 — заодно чинится PostUp
PCKEY=$(wg genkey | wg pubkey)
run "1\nPublicKey = $PCKEY\nlaptop\n\n" add-key
F=$WGS_ETC/wireguard/wg0.conf
grep -q '^PostUp' "$F" && fail "PostUp остался"
[ "$(grep -c '^PrivateKey = ' "$F")" = 1 ] || fail "PrivateKey не перенесён"
grep -q "^PrivateKey = $SERVER_KEY\$" "$F" || fail "не тот ключ сервера"
ls "$WGS_ETC"/wireguard/wg0.conf.bak.* >/dev/null || fail "нет резервной копии"
grep -q "^PublicKey = $PCKEY\$" "$F" || fail "ключ ПК не записан"
grep -q '^AllowedIPs = 10.9.0.3/32$' "$F" || fail "IP ПК: ожидался 10.9.0.3 (10.9.0.2 занят)"
grep -q 'Публичный ключ сервера' "$T/out" || fail "нет подсказки для мастера"
pass "добавление по ключу и починка PostUp"

# 3. повтор того же ключа и занятый IP — отказ
printf '1\n%s\n' "$PCKEY" >"$T/answers"
if WGS_TTY=$T/answers bash "$SCRIPT" add-key >"$T/out" 2>&1; then fail "повторный ключ принят"; fi
grep -q 'уже есть' "$T/out" || fail "нет сообщения о повторе"
pass "повторный ключ отклонён"

# 4. список
run '' list
grep -q 'phone' "$T/out" && grep -q 'laptop' "$T/out" && grep -q 'ПК' "$T/out" || { cat "$T/out"; fail "список"; }
pass "список устройств"

# 5. удалить телефон
run '2\n1\ny\n' remove
grep -q '# phone' "$WGS_ETC/amnezia/amneziawg/awg0.conf" && fail "телефон остался в файле"
grep -q '^Jc = 4$' "$WGS_ETC/amnezia/amneziawg/awg0.conf" || fail "удаление задело [Interface]"
[ -f "$C" ] && fail "конфиг телефона не удалён"
grep -q '^awg set awg0 peer .* remove$' "$LOG" || fail "awg set remove не вызван"
pass "удаление устройства"

# 6. ПК при удалении не задет: удаляем laptop, «ПК» остаётся
run '1\n2\ny\n' remove
grep -q "$PCKEY" "$F" && fail "laptop остался"
grep -q 'gc2S60E5' "$F" || fail "удалился не тот пир"
pass "удаляется только выбранный пир"

# 7. новый туннель AmneziaWG: установка и служба — заглушки
for t in apt-get add-apt-repository modprobe sysctl ufw; do printf '#!/bin/sh
exit 0
' >"$T/bin/$t"; chmod +x "$T/bin/$t"; done
printf '#!/bin/sh
echo "systemctl $*" >>"$LOG"
' >"$T/bin/systemctl"; chmod +x "$T/bin/systemctl"
printf '#!/bin/sh
echo "UNCONN 0 0 0.0.0.0:51822 0.0.0.0:*"
' >"$T/bin/ss"; chmod +x "$T/bin/ss"
printf '#!/bin/sh
echo 203.0.113.7
' >"$T/bin/curl"; chmod +x "$T/bin/curl"
run '1


y

n
' new
N=$WGS_ETC/amnezia/amneziawg/awg1.conf
[ -f "$N" ] || { cat "$T/out"; fail "awg1.conf не создан"; }
grep -q '^Address = 10.8.0.1/24$' "$N" || fail "подсеть: ожидалась свободная 10.8 ($(grep Address "$N"))"
grep -q '^ListenPort = 51823$' "$N" || fail "порт: 51821 занят awg0, 51822 — другим процессом ($(grep ListenPort "$N"))"
grep -q '^PrivateKey = ' "$N" || fail "ключ не в файле"
grep -q '^PostUp = iptables -t nat -A POSTROUTING -s 10.8.0.0/24' "$N" || fail "NAT"
for k in Jc Jmin Jmax S1 S2 H1 H2 H3 H4; do grep -q "^$k = [0-9]" "$N" || fail "нет $k"; done
[ "$(grep -E '^H[1-4] = ' "$N" | cut -d' ' -f3 | sort -u | wc -l)" = 4 ] || fail "H1–H4 должны различаться"
[ "$(stat -c %a "$N")" = 600 ] || fail "права на конфиг сервера"
grep -q 'systemctl enable --now awg-quick@awg1' "$LOG" || fail "служба не включена"
grep -q 'ip_forward = 1' "$WGS_ETC/sysctl.d/99-wgswitch.conf" || fail "ip_forward"
[ "$(cat "$WGS_ETC/wgswitch/awg1.endpoint")" = 203.0.113.7 ] || fail "адрес сервера не определён"
pass "новый туннель AmneziaWG"

echo "все проверки пройдены"
