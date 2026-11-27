say [apace-event] collect_salmon
scoreboard players remove @s ap_salmon 1
execute if score @s ap_salmon matches 1.. run function apace_challenge_events:ev/salmon
