say [apace-event] oak_log
scoreboard players remove @s ap_log_oak 1
execute if score @s ap_log_oak matches 1.. run function apace_challenge_events:ev/log_oak
