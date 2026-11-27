execute if predicate apace_challenge_events:holding_golden_pickaxe run say [apace-event] hostile_gold_pickaxe
execute if entity @s[tag=ap_tnt] run say [apace-event] hostile_tnt
scoreboard players remove @s ap_h_magma_cube 1
execute if score @s ap_h_magma_cube matches 1.. run function apace_challenge_events:ev/h_magma_cube
