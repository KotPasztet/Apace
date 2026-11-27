say [apace-event] dark_oak_slab
scoreboard players remove @s ap_slab_c 1
execute if score @s ap_slab_c matches 1.. run function apace_challenge_events:ev/slab_crafted
